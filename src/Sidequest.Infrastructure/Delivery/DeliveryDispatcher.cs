using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Administration;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.Infrastructure.Delivery;

/// <summary>Serializes recipient submissions using a SQL session lock, persists uncertainty before I/O, and fences all final writes.</summary>
/// <param name="factory">Per-operation SQL contexts.</param>
/// <param name="policy">Send-time recipient eligibility.</param>
/// <param name="renderer">Recipient-only calendar renderer.</param>
/// <param name="gateway">Real configured email transport; never called inside a SQL transaction.</param>
/// <param name="clock">Current UTC time.</param>
/// <param name="options">Bounded reminder lateness.</param>
public sealed class DeliveryDispatcher(ISidequestDbContextFactory factory, RecipientPolicy policy,
    IRecipientCalendarRenderer renderer, IEmailGateway gateway, TimeProvider clock, DurableWorkOptions options)
{
    /// <summary>Submits one leased delivery after current authorization, preference, lifecycle and ordering checks.</summary>
    /// <param name="lease">Original SQL claim proof.</param>
    /// <param name="cancellationToken">Cancels waiting; cancellation after submission remains uncertain.</param>
    /// <returns>A task completing after receipt persistence or safe supersession, not mailbox arrival.</returns>
    public async Task ExecuteAsync(WorkLease lease, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteCoreAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    private async Task ExecuteCoreAsync(WorkLease lease, CancellationToken cancellationToken)
    {
        await using var lockContext = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var row = await lockContext.NotificationDeliveries.AsNoTracking().SingleAsync(x => x.Id == lease.Id, cancellationToken).ConfigureAwait(false);
        var payload = Parse(row.PayloadJson, row.UserId);
        var eventId = payload.Change.EventId;
        if (payload.Change.QuestId is Guid questId)
        {
            var actualParent = await lockContext.Quests.AsNoTracking().Where(x => x.Id == questId).Select(x => (Guid?)x.EventId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (actualParent != eventId)
                throw ChangeOutboxHandler.InvalidPayload();
        }
        var resource = $"sidequest.delivery:{payload.Change.QuestId?.ToString("N") ?? "event"}:{row.UserId:N}";
        if (lockContext is not DbContext lockDb || !lockDb.Database.IsSqlServer())
            throw new InvalidOperationException("Calendar serialization requires SQL Server.");
        await lockDb.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lockDb.Database.GetDbConnection().CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @result;";
        command.Parameters.Add(new SqlParameter("@resource", resource));
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (result < 0)
            throw new DeliveryTransportException(TransportOutcome.Retryable, "Recipient submission is already in progress.");
        try
        {
            var message = await PrepareAsync(lease, eventId, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return;
            // Prepare committed uncertainty and disposed its SQL transaction before this external call.
            var receipt = await gateway.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(receipt.ProviderMessageId))
                throw new DeliveryTransportException(TransportOutcome.Uncertain, "Provider returned no acceptance identifier.");
            await RecordReceiptAsync(lease, eventId, receipt.ProviderMessageId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Session-scoped locks must be released before a pooled connection is returned.
            await using var release = lockDb.Database.GetDbConnection().CreateCommand();
            release.CommandText = "EXEC sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session';";
            release.Parameters.Add(new SqlParameter("@resource", resource));
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<EmailMessage?> PrepareAsync(WorkLease lease, Guid eventId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        var row = await db.NotificationDeliveries.SingleAsync(x => x.Id == lease.Id, cancellationToken).ConfigureAwait(false);
        if (!Owns(row, lease))
            return null;
        if (!string.IsNullOrWhiteSpace(row.ProviderMessageId))
            return null;
        var payload = Parse(row.PayloadJson, row.UserId);
        var change = payload.Change;
        if (change.EventId != eventId)
            throw ChangeOutboxHandler.InvalidPayload();
        var now = clock.GetUtcNow();
        if (!await policy.EligibleAsync(db, row.UserId, cancellationToken).ConfigureAwait(false))
        {
            if (payload.Calendar is { } unavailableCalendar)
            {
                var outstanding = await db.CalendarDeliveryStates.SingleOrDefaultAsync(x =>
                    x.QuestId == unavailableCalendar.QuestId && x.UserId == row.UserId, cancellationToken).ConfigureAwait(false);
                if (outstanding is { MayHaveBeenDelivered: true } && outstanding.IntendedSequence == unavailableCalendar.Sequence)
                {
                    if (unavailableCalendar.Method == "CANCEL")
                        throw new DeliveryTransportException(TransportOutcome.Permanent,
                            "Recipient is unavailable; calendar withdrawal requires authorized recovery.");
                    var quest = await db.Quests.SingleAsync(x => x.Id == unavailableCalendar.QuestId, cancellationToken).ConfigureAwait(false);
                    StageCompensation(db, row, payload, outstanding, quest);
                }
            }
            Supersede(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var user = await db.Users.SingleAsync(x => x.Id == row.UserId, cancellationToken).ConfigureAwait(false);
        var preference = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.UserId == row.UserId, cancellationToken).ConfigureAwait(false);
        var allowed = await policy.CanReceiveAsync(db, change, row.UserId, now, cancellationToken).ConfigureAwait(false);
        if (change.Kind == NotificationKind.Reminder)
            allowed &= await ReminderEligibleAsync(db, payload, preference, cancellationToken).ConfigureAwait(false);
        CalendarDeliveryState? state = null;
        if (payload.Calendar is { } snapshot)
        {
            state = await db.CalendarDeliveryStates.SingleOrDefaultAsync(x => x.QuestId == snapshot.QuestId &&
                x.UserId == row.UserId, cancellationToken).ConfigureAwait(false);
            if (state is null || state.IntendedSequence != snapshot.Sequence || state.IntendedMethod != snapshot.Method ||
                state.SentSequence >= snapshot.Sequence)
                allowed = false;
            else if (snapshot.Method == "CANCEL")
                allowed = true;
            else
            {
                var quest = await db.Quests.SingleAsync(x => x.Id == snapshot.QuestId, cancellationToken).ConfigureAwait(false);
                var parent = await db.Events.SingleAsync(x => x.Id == quest.EventId, cancellationToken).ConfigureAwait(false);
                var canRead = await policy.CanReadQuestAsync(db, quest, row.UserId, cancellationToken).ConfigureAwait(false);
                var joined = await db.Participations.AnyAsync(x => x.QuestId == quest.Id && x.UserId == row.UserId &&
                    x.Status == ParticipationStatus.Joined, cancellationToken).ConfigureAwait(false);
                var current = quest.Status == QuestStatus.Active && parent.Status == EventStatus.Active &&
                    quest.EndUtc > now && TimeRules.EventWindow(parent.StartDate, parent.EndDate, parent.TimeZoneId).End > now &&
                    canRead && joined;
                if (!current)
                {
                    var withdrawalRequired = !canRead || !joined || quest.Status is QuestStatus.Suspended or QuestStatus.Cancelled ||
                        (parent.Status == EventStatus.Cancelled && quest.EndUtc > now);
                    if (state.MayHaveBeenDelivered && withdrawalRequired)
                        StageCompensation(db, row, payload, state, quest);
                    allowed = false;
                }
                else if (quest.StartUtc != snapshot.StartUtc || quest.EndUtc != snapshot.EndUtc ||
                    quest.Title != snapshot.Title || quest.Description != snapshot.Description || quest.Location != snapshot.Location)
                {
                    // Newer durable changes own the replacement. Never intentionally send an older appointment.
                    allowed = false;
                }
            }
        }
        if (!payload.Mandatory && payload.Calendar is null)
        {
            allowed &= change.Kind switch
            {
                NotificationKind.QuestPublished => await db.EventNotificationPreferences.Where(x => x.EventId == change.EventId &&
                    x.UserId == row.UserId).Select(x => (bool?)x.NewQuestEmail).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                    ?? preference?.NewQuestEmail ?? false,
                NotificationKind.Reminder => preference?.RemindersEnabled ?? true,
                _ => preference?.ActivityEmail ?? true
            };
        }
        if (!allowed)
        {
            Supersede(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (payload.Calendar is { } calendar)
        {
            if (!string.Equals(user.Email, calendar.Recipient, StringComparison.OrdinalIgnoreCase))
                throw new DeliveryTransportException(TransportOutcome.Permanent, "Trusted recipient address changed; calendar intent requires investigation.");
            if (payload.CalendarContent is null)
            {
                payload = payload with { CalendarContent = renderer.Render(calendar) };
                row.PayloadJson = JsonSerializer.Serialize(payload);
            }
            state!.MayHaveBeenDelivered = true;
        }
        if (payload.BusinessEmail is null)
        {
            try
            {
                payload = payload with
                {
                    BusinessEmail = await BusinessEmailComposer.ComposeAsync(db, change.Kind, cancellationToken).ConfigureAwait(false)
                };
                row.PayloadJson = JsonSerializer.Serialize(payload);
            }
            catch (DomainException exception) when (exception.Code == ErrorCode.Validation)
            {
                throw new DeliveryTransportException(TransportOutcome.Permanent,
                    "Business email configuration or template is invalid. Correct it before replaying the delivery.");
            }
        }
        // A started attempt is conservatively uncertain even if cancellation is observed in the next instruction.
        row.LastError = "Submission started; acceptance is not yet recorded.";
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var email = payload.BusinessEmail;
        return new(user.Email, email.Subject, email.HtmlBody, email.TextBody, row.DeduplicationKey,
            payload.CalendarContent, payload.Calendar?.Method, email.ReplyTo);
    }

    private void StageCompensation(ISidequestDbContext db, NotificationDelivery original, DeliveryPayload payload,
        CalendarDeliveryState state, Quest quest)
    {
        var sequence = checked(Math.Max(quest.CalendarRevision, state.IntendedSequence) + 1);
        quest.CalendarRevision = sequence;
        var snapshot = payload.Calendar! with
        {
            Sequence = sequence,
            Method = "CANCEL",
            StampUtc = clock.GetUtcNow(),
            Title = "Sidequest appointment withdrawn",
            Description = "",
            Location = ""
        };
        state.IntendedSequence = sequence;
        state.IntendedMethod = "CANCEL";
        state.Payload = JsonSerializer.Serialize(snapshot);
        state.ChangedUtc = clock.GetUtcNow();
        var change = payload.Change with
        {
            Kind = NotificationKind.AccessRemoved,
            CalendarRevision = sequence,
            RecipientIds = [original.UserId],
            PreviousAttendeeIds = [original.UserId],
            AffectedUserIds = [original.UserId]
        };
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            NotificationId = original.NotificationId,
            UserId = original.UserId,
            DeduplicationKey = $"calendar:{quest.Id:N}:{original.UserId:N}:{sequence}:CANCEL",
            DueUtc = clock.GetUtcNow(),
            PayloadJson = JsonSerializer.Serialize(new DeliveryPayload(1, change, original.UserId, true, snapshot))
        });
    }

    private async Task<bool> ReminderEligibleAsync(ISidequestDbContext db, DeliveryPayload payload,
        NotificationPreference? preference, CancellationToken cancellationToken)
    {
        if (preference is { RemindersEnabled: false })
            return false;
        var work = await db.ScheduledWork.SingleOrDefaultAsync(x => x.Id == payload.Change.ChangeId &&
            x.Type == WorkTypes.Reminder, cancellationToken).ConfigureAwait(false);
        if (work is null)
            return false;
        var reminder = ReminderPayload.Parse(work.PayloadJson);
        if (reminder.UserId != payload.RecipientId || reminder.QuestId != payload.Change.QuestId ||
            reminder.UserId != work.UserId || reminder.QuestId != work.QuestId)
            throw ChangeOutboxHandler.InvalidPayload();
        var now = clock.GetUtcNow();
        if (reminder.ScheduledUtc > now || now - reminder.ScheduledUtc > options.ReminderLateness || reminder.StartUtc <= now)
            return false;
        return await db.Quests.AnyAsync(x => x.Id == reminder.QuestId && x.StartRevision == reminder.StartRevision &&
            x.StartUtc == reminder.StartUtc && x.Status == QuestStatus.Active &&
            db.Events.Any(e => e.Id == x.EventId && e.Status == EventStatus.Active) &&
            db.Participations.Any(p => p.QuestId == x.Id && p.UserId == payload.RecipientId &&
                p.Status == ParticipationStatus.Joined), cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordReceiptAsync(WorkLease lease, Guid eventId, string providerId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        var row = await db.NotificationDeliveries.SingleAsync(x => x.Id == lease.Id, cancellationToken).ConfigureAwait(false);
        if (!Owns(row, lease))
            return;
        row.ProviderMessageId = providerId.Length <= 2000 ? providerId : providerId[..2000];
        var payload = Parse(row.PayloadJson, row.UserId);
        if (payload.Change.EventId != eventId)
            throw ChangeOutboxHandler.InvalidPayload();
        if (payload.Calendar is { } snapshot)
        {
            var state = await db.CalendarDeliveryStates.SingleAsync(x => x.QuestId == snapshot.QuestId &&
                x.UserId == row.UserId, cancellationToken).ConfigureAwait(false);
            state.SentSequence = Math.Max(state.SentSequence ?? -1, snapshot.Sequence);
            state.MayHaveBeenDelivered = snapshot.Method != "CANCEL" || state.IntendedSequence > snapshot.Sequence;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool Owns(NotificationDelivery row, WorkLease lease) =>
        row.Status == WorkStatus.Processing && row.LeaseId == lease.Token && row.LeaseUntilUtc > clock.GetUtcNow();

    private static void Supersede(NotificationDelivery row)
    {
        row.Status = WorkStatus.Superseded;
        row.LeaseId = null;
        row.LeaseUntilUtc = null;
    }

    private static DeliveryPayload Parse(string json, Guid recipient)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<DeliveryPayload>(json);
            if (payload is null || payload.Version != 1 || payload.RecipientId != recipient || payload.Change is null ||
                !Enum.IsDefined(payload.Change.Kind) || payload.Change.ChangeId == Guid.Empty ||
                payload.Change.RecipientIds is null ||
                (!payload.Change.RecipientIds.Contains(recipient) && !(payload.Change.PreviousAttendeeIds?.Contains(recipient) ?? false)) ||
                (payload.Calendar is { } snapshot && snapshot.QuestId != payload.Change.QuestId) ||
                payload.Calendar is { Method: not ("REQUEST" or "CANCEL") })
                throw ChangeOutboxHandler.InvalidPayload();
            ChangeOutboxHandler.Validate(payload.Change);
            if (ChangeOutboxHandler.IsTargeted(payload.Change.Kind) && !payload.Change.AffectedUserIds!.Contains(recipient))
                throw ChangeOutboxHandler.InvalidPayload();
            if (payload.BusinessEmail is not null)
                BusinessEmailRules.ValidateSnapshot(payload.BusinessEmail, payload.Change.Kind);
            return payload;
        }
        catch (JsonException)
        {
            throw ChangeOutboxHandler.InvalidPayload();
        }
        catch (DomainException exception) when (exception.Code == ErrorCode.Validation)
        {
            throw ChangeOutboxHandler.InvalidPayload();
        }
    }
}
