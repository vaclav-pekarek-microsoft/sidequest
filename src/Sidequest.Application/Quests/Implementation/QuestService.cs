using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Media.Implementation;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Quests.Implementation;

/// <summary>Authorizes and persists Quest workflows with operation-scoped, serializable SQL transactions.</summary>
/// <param name="factory">Creates contexts that are never retained by an interactive circuit.</param>
/// <param name="access">Rechecks current identity, eligibility, individual membership, and resource grants.</param>
/// <param name="writer">Stages delivery intent atomically with state and audit records.</param>
/// <param name="clock">Provides deterministic effective-time boundaries.</param>
/// <param name="eventLifecycle">Stages Event-owned overdue completion in the locked caller transaction.</param>
public sealed class QuestService(ISidequestDbContextFactory factory, IResourceAccess access,
    IChangeWriter writer, TimeProvider clock, IEventLifecycleReconciler eventLifecycle) : IQuestService
{
    /// <inheritdoc />
    public Task<PageResult<QuestSummary>> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page,
        CancellationToken cancellationToken = default) =>
        ListAsync(kind, eventId, page, new QuestDateFilter(), cancellationToken);

    /// <inheritdoc />
    public async Task<PageResult<QuestSummary>> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page,
        QuestDateFilter dates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(dates);
        if (dates.FromUtc is not null && dates.UntilUtc is not null && dates.UntilUtc <= dates.FromUtc)
            throw new DomainException(ErrorCode.Validation, "The end of the date range must follow its start.", "Dates");
        if (!Enum.IsDefined(kind))
            throw new DomainException(ErrorCode.Validation, "Unknown Quest view.", "Kind");
        var offset = page.Offset;
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        if (eventId is not null)
            await access.RequireEventAsync(db, eventId.Value, actor.Id, kind == QuestListKind.Moderation,
                cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var moderation = kind == QuestListKind.Moderation;
        var query = Visible(db, actor.Id, moderation).Where(q => eventId == null || q.EventId == eventId);
        if (dates.FromUtc is not null)
            query = query.Where(q => q.StartUtc >= dates.FromUtc.Value);
        if (dates.UntilUtc is not null)
            query = query.Where(q => q.StartUtc < dates.UntilUtc.Value);
        query = kind switch
        {
            QuestListKind.Joined => query.Where(q => db.Participations.Any(p => p.QuestId == q.Id &&
                p.UserId == actor.Id && p.Status == ParticipationStatus.Joined)),
            QuestListKind.Following => query.Where(q => db.Participations.Any(p => p.QuestId == q.Id &&
                p.UserId == actor.Id && p.Status == ParticipationStatus.Following)),
            QuestListKind.Organizing => query.Where(q => db.QuestOwners.Any(o => o.QuestId == q.Id && o.UserId == actor.Id)),
            QuestListKind.Invited => query.Where(q => q.Visibility == QuestVisibility.Private &&
                db.QuestInvitations.Any(i => i.QuestId == q.Id && i.UserId == actor.Id && i.Status == QuestInvitationStatus.Active)),
            QuestListKind.Discover => query.Where(q => q.Visibility == QuestVisibility.Public && q.Status == QuestStatus.Active &&
                db.Events.Any(e => e.Id == q.EventId && e.Status == EventStatus.Active)),
            QuestListKind.History => query.Where(q => q.EndUtc <= now || q.Status == QuestStatus.Completed ||
                q.Status == QuestStatus.Cancelled || q.Status == QuestStatus.Archived),
            _ => query
        };
        if (kind != QuestListKind.History)
            query = query.Where(q => q.EndUtc > now && q.Status != QuestStatus.Cancelled &&
                q.Status != QuestStatus.Completed && q.Status != QuestStatus.Archived);
        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await Summaries(db, query.OrderBy(q => q.StartUtc).ThenBy(q => q.Id).Skip(offset).Take(page.Limit),
            actor.Id, moderation, now).ToListAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(items, count, page.Page, page.Limit);
    }

    /// <inheritdoc />
    public async Task<QuestDetail> GetAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var parentId = moderation ? await QuestChanges.ParentIdAsync(db, id, cancellationToken).ConfigureAwait(false) : null;
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (parentId is not null)
            await db.LockEventAsync(parentId.Value, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var quest = await RequireAsync(db, id, actor.Id, false, moderation, cancellationToken).ConfigureAwait(false);
        var summary = await Summaries(db, db.Quests.Where(q => q.Id == id), actor.Id, moderation, clock.GetUtcNow())
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var owners = await (from owner in db.QuestOwners
                            join user in db.Users on owner.UserId equals user.Id
                            where owner.QuestId == id && user.IsEligible && user.DepartureVerifiedUtc == null
                            orderby user.DisplayName, user.Id
                            select new OwnerSummary(user.Id, user.DisplayName, user.Email))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var attendees = moderation ? null : await RosterAsync(db, id, ParticipationStatus.Joined, cancellationToken).ConfigureAwait(false);
        var followers = moderation || !summary.IsOwner ? null :
            await RosterAsync(db, id, ParticipationStatus.Following, cancellationToken).ConfigureAwait(false);
        var invitees = moderation || !summary.IsOwner ? null :
            await (from invitation in db.QuestInvitations
                   join user in db.Users on invitation.UserId equals user.Id
                   where invitation.QuestId == id && invitation.Status == QuestInvitationStatus.Active &&
                       user.IsEligible && user.DepartureVerifiedUtc == null
                   orderby user.DisplayName, user.Id
                   select new PersonSummary(user.Id, user.DisplayName)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (moderation && quest.Visibility == QuestVisibility.Private)
            AuditModerationRead(db, quest, actor.Id, "ModerationDetailRead");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(summary, quest.Description, quest.StatusReason, owners, attendees, followers, invitees);
    }

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(Guid eventId, QuestInput input, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await ReconcileParentAsync(eventId, cancellationToken).ConfigureAwait(false);
            await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
            var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
            var parent = await access.RequireEventAsync(db, eventId, actor.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (QuestChanges.ParentIsOverdue(parent, now))
                continue;
            QuestChanges.RequireActive(parent, now);
            var quest = new Quest { EventId = eventId, CreatorId = actor.Id, CreatedUtc = now, UpdatedUtc = now, Status = QuestStatus.Draft };
            Configure(quest, parent, input, now);
            db.Quests.Add(quest);
            db.QuestOwners.Add(new QuestOwner { QuestId = quest.Id, UserId = actor.Id });
            QuestChanges.Audit(db, quest, actor.Id, "DraftCreated", "", now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return quest.Id;
        }
    }

    /// <inheritdoc />
    public Task EditAsync(Guid id, string version, QuestInput input, CancellationToken cancellationToken = default) =>
        MutateAsync(id, true, false, async (db, quest, parent, actor, now, token) =>
        {
            ArgumentNullException.ThrowIfNull(input);
            InputRules.Version(quest, version);
            QuestChanges.RequireActive(parent, now);
            if (quest.Status is not (QuestStatus.Draft or QuestStatus.Active or QuestStatus.Suspended))
                throw Conflict("This Quest cannot be edited.");
            var oldStart = quest.StartUtc;
            var oldEnd = quest.EndUtc;
            var calendar = quest.Title != input.Title?.Trim() || quest.Description != input.Description?.Trim() ||
                quest.Location != input.Location?.Trim();
            var material = quest.Location != input.Location?.Trim();
            var audience = await QuestChanges.CaptureAsync(db, id, token).ConfigureAwait(false);
            Configure(quest, parent, input, now);
            var timesChanged = oldStart != quest.StartUtc || oldEnd != quest.EndUtc;
            calendar |= timesChanged;
            material |= timesChanged;
            if (oldStart != quest.StartUtc)
                quest.StartRevision++;
            if (calendar && quest.Status != QuestStatus.Draft)
                quest.CalendarRevision++;
            var change = QuestChanges.Audit(db, quest, actor, "ContentEdited", "", now);
            if (quest.Status == QuestStatus.Active)
                QuestChanges.Notify(db, writer, quest, change, actor, NotificationKind.QuestUpdated,
                    audience.Owners.Concat(audience.Attendees).Concat(audience.Followers), now,
                    previousAttendees: audience.Attendees, calendarChanged: calendar, material: material);
            if (quest.Status == QuestStatus.Suspended)
            {
                var moderators = await db.EventOwners.Where(x => x.EventId == parent.Id)
                    .Select(x => x.UserId).ToArrayAsync(token).ConfigureAwait(false);
                QuestChanges.Notify(db, writer, quest, change, actor, NotificationKind.SuspendedQuestEdited,
                    audience.Owners.Concat(moderators), now);
            }
            if (quest.Status != QuestStatus.Draft && oldEnd != quest.EndUtc)
                await QuestChanges.ScheduleAsync(db, quest, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public async Task ChangeStatusAsync(Guid id, string version, QuestStatus target, string reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(target))
            throw new DomainException(ErrorCode.Validation, "Unknown Quest status.", "Status");
        // Publication and reinstatement share a target, but never share an authorization grant.
        await using var probe = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireUserAsync(probe, cancellationToken).ConfigureAwait(false);
        var current = await probe.Quests.Where(q => q.Id == id).Select(q => (QuestStatus?)q.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var moderation = target == QuestStatus.Suspended || target == QuestStatus.Active && current == QuestStatus.Suspended;
        await MutateAsync(id, !moderation, moderation, async (db, quest, parent, actor, now, token) =>
        {
            InputRules.Version(quest, version);
            if (quest.Status == target)
                return;
            var allowed = (quest.Status, target) switch
            {
                (QuestStatus.Draft, QuestStatus.Active) => !moderation,
                (QuestStatus.Active, QuestStatus.Suspended) => moderation,
                (QuestStatus.Suspended, QuestStatus.Active) => moderation,
                (QuestStatus.Draft or QuestStatus.Active or QuestStatus.Suspended, QuestStatus.Cancelled) => !moderation,
                (QuestStatus.Completed or QuestStatus.Cancelled, QuestStatus.Archived) => !moderation,
                _ => false
            };
            if (!allowed)
                throw Conflict("This lifecycle transition is not allowed.");
            var explanation = target == QuestStatus.Suspended || quest.Status == QuestStatus.Suspended && target == QuestStatus.Active ||
                target == QuestStatus.Cancelled && quest.Status != QuestStatus.Draft ? InputRules.Reason(reason) :
                target == QuestStatus.Cancelled ? InputRules.Text(reason, "Reason", 0, 2000) : "";
            if (target is QuestStatus.Active or QuestStatus.Suspended or QuestStatus.Cancelled)
                QuestChanges.RequireActive(parent, now);
            if (target == QuestStatus.Active)
            {
                if (quest.EndUtc <= now)
                    throw Conflict("The Quest has ended.");
                InputRules.Text(quest.Location, "Location", 1, 500);
                TimeRules.ValidateQuest(quest.StartUtc, quest.EndUtc, parent.StartDate, parent.EndDate, parent.TimeZoneId);
                await RequireEligibleOwnerAsync(db, quest, null, token).ConfigureAwait(false);
            }
            await QuestChanges.TransitionAsync(db, writer, quest, target, actor, explanation, now, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default) =>
        MutateAsync(id, true, false, async (db, quest, parent, actor, now, token) =>
        {
            InputRules.Version(quest, version);
            QuestChanges.RequireActive(parent, now);
            if (quest.Status != QuestStatus.Draft ||
                await db.Participations.AnyAsync(x => x.QuestId == id, token).ConfigureAwait(false) ||
                await db.QuestStatusHistory.AnyAsync(x => x.QuestId == id, token).ConfigureAwait(false))
                throw Conflict("Only an unpublished draft without participation can be deleted.");
            await DraftMediaCleanup.StageAsync(db, quest, actor, now, token).ConfigureAwait(false);
            db.QuestOwners.RemoveRange(await db.QuestOwners.Where(x => x.QuestId == id).ToListAsync(token).ConfigureAwait(false));
            db.QuestInvitations.RemoveRange(await db.QuestInvitations.Where(x => x.QuestId == id).ToListAsync(token).ConfigureAwait(false));
            QuestChanges.Audit(db, quest, actor, "DraftDeleted", "", now);
            db.Quests.Remove(quest);
        }, cancellationToken);

    /// <inheritdoc />
    public Task ParticipateAsync(Guid id, ParticipationCommand command, CancellationToken cancellationToken = default) =>
        MutateAsync(id, false, false, async (db, quest, parent, actor, now, token) =>
        {
            if (command is ParticipationCommand.Join or ParticipationCommand.Follow)
            {
                QuestChanges.RequireActive(parent, now);
                if (quest.Status != QuestStatus.Active || now >= quest.EndUtc)
                    throw Conflict("Joining and following require an active Quest before its end.");
            }
            if (quest.Status == QuestStatus.Draft)
                throw Conflict("Draft Quests do not accept participation.");
            var participation = await db.Participations.SingleOrDefaultAsync(
                x => x.QuestId == id && x.UserId == actor, token).ConfigureAwait(false);
            var previous = participation?.Status ?? ParticipationStatus.None;
            var next = ParticipationRules.Apply(previous, command);
            if (next == previous)
                return;
            var audience = await QuestChanges.CaptureAsync(db, id, token).ConfigureAwait(false);
            if (participation is null)
            {
                participation = new QuestParticipation { QuestId = id, UserId = actor };
                db.Participations.Add(participation);
            }
            participation.Status = next;
            participation.ChangedUtc = now;
            var calendar = previous == ParticipationStatus.Joined || next == ParticipationStatus.Joined;
            if (calendar)
                quest.CalendarRevision++;
            var change = QuestChanges.Audit(db, quest, actor, $"Participation:{actor:N}:{previous}->{next}", "", now);
            if (calendar)
                QuestChanges.Notify(db, writer, quest, change, actor,
                    next == ParticipationStatus.Joined ? NotificationKind.Joined : NotificationKind.Left,
                    audience.Owners.Append(actor), now,
                    previousAttendees: previous == ParticipationStatus.Joined ? [actor] : [],
                    calendarChanged: true, affectedUsers: [actor]);
        }, cancellationToken);

    /// <inheritdoc />
    public Task InviteAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) =>
        MutateAsync(id, true, false, async (db, quest, parent, actor, now, token) =>
        {
            QuestChanges.RequireActive(parent, now);
            if (quest.Status != QuestStatus.Active || quest.Visibility != QuestVisibility.Private || quest.EndUtc <= now)
                throw Conflict("Invitations require an active private Quest before its end.");
            await RequireTargetAsync(db, parent.Id, userId, token).ConfigureAwait(false);
            var invitation = await db.FindQuestInvitationForUpdateAsync(id, userId, token).ConfigureAwait(false);
            if (invitation?.Status == QuestInvitationStatus.Active)
                return;
            if (invitation is null)
            {
                invitation = new QuestInvitation { QuestId = id, UserId = userId };
                db.QuestInvitations.Add(invitation);
            }
            invitation.Status = QuestInvitationStatus.Active;
            invitation.InvitedById = actor;
            invitation.ChangedUtc = now;
            var change = QuestChanges.Audit(db, quest, actor, $"InvitationGranted:{userId:N}", "", now);
            QuestChanges.Notify(db, writer, quest, change, actor, NotificationKind.QuestInvitation, [userId], now,
                affectedUsers: [userId]);
        }, cancellationToken);

    /// <inheritdoc />
    public Task RevokeInvitationAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default) =>
        MutateAsync(id, true, false, async (db, quest, _, actor, now, token) =>
        {
            var explanation = InputRules.Reason(reason);
            if (!await db.QuestInvitations.AnyAsync(i => i.QuestId == id && i.UserId == userId &&
                i.Status == QuestInvitationStatus.Active, token).ConfigureAwait(false))
                return;
            await QuestChanges.WithdrawAsync(db, writer, quest, userId, actor, "InvitationRevoked",
                explanation, NotificationKind.AccessRemoved, now, token, revokeInvitation: true).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task RemoveAttendeeAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default) =>
        MutateAsync(id, true, false, async (db, quest, parent, actor, now, token) =>
        {
            QuestChanges.RequireActive(parent, now);
            if (quest.Status is not (QuestStatus.Active or QuestStatus.Suspended))
                throw Conflict("Attendees can only be removed from active or suspended Quests.");
            await QuestChanges.WithdrawAsync(db, writer, quest, userId, actor, "AttendeeRemoved",
                InputRules.Reason(reason), NotificationKind.AttendeeRemoved, now, token,
                revokeInvitation: false, joinedOnly: true).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task AddOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) =>
        ChangeOwnerAsync(id, userId, true, cancellationToken);

    /// <inheritdoc />
    public Task RemoveOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) =>
        ChangeOwnerAsync(id, userId, false, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuestHistoryItem>> HistoryAsync(Guid id, bool moderation = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var parentId = moderation ? await QuestChanges.ParentIdAsync(db, id, cancellationToken).ConfigureAwait(false) : null;
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (parentId is not null)
            await db.LockEventAsync(parentId.Value, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var quest = await RequireAsync(db, id, actor.Id, false, moderation, cancellationToken).ConfigureAwait(false);
        var owner = await db.QuestOwners.AnyAsync(x => x.QuestId == id && x.UserId == actor.Id, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<QuestHistoryItem> history;
        if (owner || moderation)
        {
            // Moderators never receive action labels containing participant or invitee identifiers.
            var audit = db.AuditEntries.Where(x => x.ResourceKind == ResourceKind.Quest && x.ResourceId == id &&
                (!moderation || x.Action.StartsWith("Status:") || x.Action == "ContentEdited"));
            history = await (from entry in audit
                             join user in db.Users on entry.ActorId equals user.Id into actors
                             from user in actors.DefaultIfEmpty()
                             orderby entry.OccurredUtc descending, entry.Id
                             select new QuestHistoryItem(entry.Action, entry.Reason, entry.OccurredUtc,
                                 user == null ? null : user.DisplayName)).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Ordinary viewers get only the current participant-facing status, not moderation records.
            var parent = await db.Events.SingleAsync(e => e.Id == quest.EventId, cancellationToken).ConfigureAwait(false);
            history = [new QuestHistoryItem(QuestChanges.EffectiveStatus(quest, parent, clock.GetUtcNow()).ToString(),
                quest.StatusReason, quest.UpdatedUtc, null)];
        }
        if (moderation && quest.Visibility == QuestVisibility.Private)
            AuditModerationRead(db, quest, actor.Id, "ModerationHistoryRead");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return history;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OfflineQuest>> GetOfflineJoinedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var joined = Visible(db, actor.Id, false).Where(q =>
            db.Participations.Any(p => p.QuestId == q.Id && p.UserId == actor.Id && p.Status == ParticipationStatus.Joined));
        var result = await (from quest in joined
                            join parent in db.Events on quest.EventId equals parent.Id
                            orderby quest.StartUtc, quest.Id
                            select new OfflineQuest(quest.Id, parent.Id, quest.Title, quest.Location,
                                quest.StartUtc, quest.EndUtc, parent.TimeZoneId,
                                (quest.Status == QuestStatus.Active || quest.Status == QuestStatus.Suspended) && quest.EndUtc <= now
                                    ? QuestStatus.Completed : quest.Status)).ToListAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private Task ChangeOwnerAsync(Guid id, Guid userId, bool add, CancellationToken token) =>
        MutateAsync(id, true, false, async (db, quest, parent, actor, now, ct) =>
        {
            if (add && (quest.Status == QuestStatus.Archived || parent.Status == EventStatus.Archived))
                throw Conflict("New owners of archived resources can only be assigned through recovery.");
            var owner = await db.QuestOwners.SingleOrDefaultAsync(x => x.QuestId == id && x.UserId == userId, ct).ConfigureAwait(false);
            if (add)
                await RequireTargetAsync(db, parent.Id, userId, ct).ConfigureAwait(false);
            if (add == (owner is not null))
                return;
            var audience = await QuestChanges.CaptureAsync(db, id, ct).ConfigureAwait(false);
            if (add)
                db.QuestOwners.Add(new QuestOwner { QuestId = id, UserId = userId });
            else
            {
                await RequireEligibleOwnerAsync(db, quest, userId, ct).ConfigureAwait(false);
                db.QuestOwners.Remove(owner!);
            }
            var change = QuestChanges.Audit(db, quest, actor, $"{(add ? "OwnerAdded" : "OwnerRemoved")}:{userId:N}", "", now);
            QuestChanges.Notify(db, writer, quest, change, actor, NotificationKind.OwnershipChanged,
                audience.Owners.Append(userId), now, affectedUsers: [userId]);
            if (!add && quest.Visibility == QuestVisibility.Private &&
                !await db.QuestInvitations.AnyAsync(i => i.QuestId == id && i.UserId == userId &&
                    i.Status == QuestInvitationStatus.Active, ct).ConfigureAwait(false))
                await QuestChanges.WithdrawAsync(db, writer, quest, userId, actor, "OwnershipAccessLost",
                    "Quest ownership access was removed.", NotificationKind.AccessRemoved, now, ct,
                    revokeInvitation: false).ConfigureAwait(false);
        }, token);

    private async Task MutateAsync(Guid id, bool ownerOnly, bool moderation,
        Func<ISidequestDbContext, Quest, Event, Guid, DateTimeOffset, CancellationToken, Task> mutation, CancellationToken token)
    {
        while (true)
        {
            await ReconcileOverdueAsync(id, ownerOnly, moderation, token).ConfigureAwait(false);
            await using var db = await factory.CreateAsync(token).ConfigureAwait(false);
            var parentId = await QuestChanges.ParentIdAsync(db, id, token).ConfigureAwait(false);
            await using var transaction = await db.BeginTransactionAsync(cancellationToken: token).ConfigureAwait(false);
            if (parentId is not null)
                await db.LockEventAsync(parentId.Value, token).ConfigureAwait(false);
            var actor = await access.RequireUserAsync(db, token).ConfigureAwait(false);
            var quest = await RequireAsync(db, id, actor.Id, ownerOnly, moderation, token).ConfigureAwait(false);
            var parent = await db.Events.SingleAsync(x => x.Id == quest.EventId, token).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            // A crossed cutoff needs a fresh reconciliation transaction, not cleanup rolled back by a rejected command.
            if (QuestChanges.ParentIsOverdue(parent, now) ||
                quest.Status is QuestStatus.Active or QuestStatus.Suspended && now >= quest.EndUtc)
                continue;
            await mutation(db, quest, parent, actor.Id, now, token).ConfigureAwait(false);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return;
        }
    }

    private async Task ReconcileOverdueAsync(Guid id, bool ownerOnly, bool moderation, CancellationToken token)
    {
        await using var db = await factory.CreateAsync(token).ConfigureAwait(false);
        var parentId = await QuestChanges.ParentIdAsync(db, id, token).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: token).ConfigureAwait(false);
        if (parentId is not null)
            await db.LockEventAsync(parentId.Value, token).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, token).ConfigureAwait(false);
        var quest = await RequireAsync(db, id, actor.Id, ownerOnly, moderation, token).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var changed = await eventLifecycle.ReconcileAsync(db, quest.EventId, now, token).ConfigureAwait(false);
        if (quest.Status is QuestStatus.Active or QuestStatus.Suspended && now >= quest.EndUtc)
        {
            await QuestChanges.TransitionAsync(db, writer, quest, QuestStatus.Completed, null,
                "The Quest end time has been reached.", now, token).ConfigureAwait(false);
            changed = true;
        }
        if (changed)
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async Task ReconcileParentAsync(Guid eventId, CancellationToken token)
    {
        await using var db = await factory.CreateAsync(token).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: token).ConfigureAwait(false);
        await db.LockEventAsync(eventId, token).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, token).ConfigureAwait(false);
        await access.RequireEventAsync(db, eventId, actor.Id, cancellationToken: token).ConfigureAwait(false);
        if (await eventLifecycle.ReconcileAsync(db, eventId, clock.GetUtcNow(), token).ConfigureAwait(false))
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async Task<Quest> RequireAsync(ISidequestDbContext db, Guid id, Guid actor, bool owner,
        bool moderation, CancellationToken token)
    {
        var quest = await access.RequireQuestAsync(db, id, actor, owner, moderation, token).ConfigureAwait(false);
        if (!await Visible(db, actor, moderation, owner).AnyAsync(q => q.Id == id, token).ConfigureAwait(false))
            throw new DomainException(ErrorCode.NotFound, "This resource is unavailable.");
        return quest;
    }

    private static IQueryable<Quest> Visible(ISidequestDbContext db, Guid actor, bool moderation, bool ownerOnly = false) =>
        db.Quests.Where(q =>
            db.Events.Any(e => e.Id == q.EventId && e.Status != EventStatus.Draft &&
                (db.EventOwners.Any(o => o.EventId == e.Id && o.UserId == actor) ||
                 !db.EventStatusHistory.Any(h => h.EventId == e.Id &&
                     h.Previous == EventStatus.Draft && h.Next == EventStatus.Cancelled))) &&
            db.EventMemberships.Any(m => m.EventId == q.EventId && m.UserId == actor && m.Status == MembershipStatus.Active) &&
            (ownerOnly && !moderation
                ? db.QuestOwners.Any(o => o.QuestId == q.Id && o.UserId == actor)
                : moderation
                ? q.Status != QuestStatus.Draft &&
                    !db.QuestStatusHistory.Any(h => h.QuestId == q.Id && h.Previous == QuestStatus.Draft && h.Next == QuestStatus.Cancelled) &&
                    db.EventOwners.Any(o => o.EventId == q.EventId && o.UserId == actor)
                : db.QuestOwners.Any(o => o.QuestId == q.Id && o.UserId == actor) ||
                    q.Status != QuestStatus.Draft &&
                    !db.QuestStatusHistory.Any(h => h.QuestId == q.Id && h.Previous == QuestStatus.Draft && h.Next == QuestStatus.Cancelled) &&
                    (q.Visibility == QuestVisibility.Public ||
                     db.QuestInvitations.Any(i => i.QuestId == q.Id && i.UserId == actor && i.Status == QuestInvitationStatus.Active))));

    private static IQueryable<QuestSummary> Summaries(ISidequestDbContext db, IQueryable<Quest> quests,
        Guid actor, bool moderation, DateTimeOffset now) =>
        from quest in quests
        join parent in db.Events on quest.EventId equals parent.Id
        select new QuestSummary(quest.Id, parent.Id, parent.Name, quest.Title, quest.Location,
            quest.StartUtc, quest.EndUtc, parent.TimeZoneId,
            (quest.Status == QuestStatus.Active || quest.Status == QuestStatus.Suspended) && quest.EndUtc <= now
                ? QuestStatus.Completed : quest.Status,
            quest.Visibility,
            moderation ? null : db.Participations.Count(p => p.QuestId == quest.Id && p.Status == ParticipationStatus.Joined &&
                db.Users.Any(u => u.Id == p.UserId && u.IsEligible && u.DepartureVerifiedUtc == null)),
            moderation ? null : db.Participations.Count(p => p.QuestId == quest.Id && p.Status == ParticipationStatus.Following &&
                db.Users.Any(u => u.Id == p.UserId && u.IsEligible && u.DepartureVerifiedUtc == null)),
            quest.SuggestedCapacity,
            moderation ? ParticipationStatus.None : db.Participations.Where(p => p.QuestId == quest.Id && p.UserId == actor)
                .Select(p => p.Status).FirstOrDefault(),
            db.QuestOwners.Any(o => o.QuestId == quest.Id && o.UserId == actor),
            db.EventOwners.Any(o => o.EventId == parent.Id && o.UserId == actor),
            Convert.ToBase64String(quest.Version), quest.CoverAssetId);

    private static async Task<List<PersonSummary>> RosterAsync(ISidequestDbContext db, Guid id,
        ParticipationStatus status, CancellationToken token) =>
        await (from participation in db.Participations
               join user in db.Users on participation.UserId equals user.Id
               where participation.QuestId == id && participation.Status == status &&
                   user.IsEligible && user.DepartureVerifiedUtc == null
               orderby user.DisplayName, user.Id
               select new PersonSummary(user.Id, user.DisplayName)).ToListAsync(token).ConfigureAwait(false);

    private static async Task RequireTargetAsync(ISidequestDbContext db, Guid eventId, Guid userId, CancellationToken token)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsEligible && u.DepartureVerifiedUtc == null &&
            db.EventMemberships.Any(m => m.EventId == eventId && m.UserId == userId && m.Status == MembershipStatus.Active),
            token).ConfigureAwait(false))
            throw new DomainException(ErrorCode.Validation, "Choose an eligible current Event member.", "UserId");
    }

    private static async Task RequireEligibleOwnerAsync(ISidequestDbContext db, Quest quest, Guid? excluding, CancellationToken token)
    {
        if (!await db.QuestOwners.AnyAsync(o => o.QuestId == quest.Id && o.UserId != excluding &&
            db.Users.Any(u => u.Id == o.UserId && u.IsEligible && u.DepartureVerifiedUtc == null) &&
            db.EventMemberships.Any(m => m.EventId == quest.EventId && m.UserId == o.UserId && m.Status == MembershipStatus.Active),
            token).ConfigureAwait(false))
            throw Conflict("At least one eligible Event member must remain a Quest owner.");
    }

    private static void Configure(Quest quest, Event parent, QuestInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(input.Visibility))
            throw new DomainException(ErrorCode.Validation, "Choose Public or Private visibility.", "Visibility");
        if (quest.Status != QuestStatus.Draft && input.Visibility != quest.Visibility)
            throw Conflict("Visibility cannot change after publication.");
        var title = InputRules.Text(input.Title, "Title", 3, 120);
        var description = InputRules.Text(input.Description, "Description", 0, 10000);
        var location = InputRules.Text(input.Location, "Location", quest.Status == QuestStatus.Draft ? 0 : 1, 500);
        InputRules.Capacity(input.SuggestedCapacity);
        var start = TimeRules.ToUtc(input.StartLocal, parent.TimeZoneId, input.StartOffset);
        var end = TimeRules.ToUtc(input.EndLocal, parent.TimeZoneId, input.EndOffset);
        TimeRules.ValidateQuest(start, end, parent.StartDate, parent.EndDate, parent.TimeZoneId);
        if (quest.Status != QuestStatus.Draft && end <= now)
            throw new DomainException(ErrorCode.Validation, "The Quest end must be in the future.", "EndLocal");
        quest.Title = title;
        quest.Description = description;
        quest.Location = location;
        quest.SuggestedCapacity = input.SuggestedCapacity;
        quest.Visibility = input.Visibility;
        quest.StartUtc = start;
        quest.EndUtc = end;
    }

    private void AuditModerationRead(ISidequestDbContext db, Quest quest, Guid actor, string action) =>
        db.AuditEntries.Add(new AuditEntry
        {
            ResourceKind = ResourceKind.Quest,
            ResourceId = quest.Id,
            ActorId = actor,
            Action = action,
            OccurredUtc = clock.GetUtcNow(),
            CorrelationId = Guid.NewGuid().ToString("N")
        });

    private static DomainException Conflict(string message) => new(ErrorCode.Conflict, message);
}
