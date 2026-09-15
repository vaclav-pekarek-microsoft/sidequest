using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreComposition;

/// <summary>Proves producer-driven cancellation, coalesced status audiences and recipient-only calendar withdrawals.</summary>
public sealed class EventCancellationCompositionTests
{
    /// <summary>Cancels multiple real children with overlapping roles, excludes ineffective audiences, and delivers exact safe status and calendar outcomes.</summary>
    /// <returns>A task completing after fresh SQL state, audit, durable intent and actual gateway assertions.</returns>
    [Fact]
    public async Task CancelEvent_RealProducers_CoalesceParentStatusAndPerQuestCalendarWithdrawals()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner1 = await s.AddUserAsync("owner-one");
        var owner2 = await s.AddUserAsync("owner-two");
        var attendee1 = await s.AddUserAsync("attendee-one");
        var attendee2 = await s.AddUserAsync("attendee-two", registered: true);
        var follower = await s.AddUserAsync("follower");
        var invitee = await s.AddUserAsync("invitee");
        var registered = await s.AddUserAsync("registered-control", registered: true);
        await s.AddUserAsync("unregistered-control");
        var ineligible = await s.AddUserAsync("ineligible", registered: true);
        var departed = await s.AddUserAsync("departed", registered: true);
        var removed = await s.AddUserAsync("removed", registered: true);
        var foreign = await s.AddUserAsync("foreign", registered: true);
        var draftOnly = await s.AddUserAsync("draft-only");
        var endedOnly = await s.AddUserAsync("ended-only");
        var q1 = await s.CreateQuestAsync(owner1, QuestVisibility.Private);
        await s.Quests.InviteAsync(q1, attendee1.Id);
        await s.Quests.InviteAsync(q1, invitee.Id);
        var q2 = await s.CreateQuestAsync(owner2);
        var empty = await s.CreateQuestAsync(owner2);
        var draft = await s.CreateQuestAsync(draftOnly, publish: false);
        await s.ParticipateAsync(q1, attendee1, ParticipationCommand.Join);
        await s.ParticipateAsync(q2, attendee1, ParticipationCommand.Join);
        await s.ParticipateAsync(q2, attendee2, ParticipationCommand.Join);
        await s.ParticipateAsync(q2, follower, ParticipationCommand.Follow);
        s.ActAs(owner2);
        await s.Quests.EditAsync(q2, await s.QuestVersionAsync(q2),
            CoreCompositionScenario.QuestInput(location: "Updated private room sentinel"));
        await s.FlushAsync();
        var old1 = await s.QuestAsync(q1);
        var old2 = await s.QuestAsync(q2);
        Assert.NotEqual(old1.CalendarRevision, old2.CalendarRevision);
        var historical = FoundationSeed.NewQuest(s.EventId, endedOnly.Id);
        historical.StartUtc = s.Clock.Now.AddHours(-1);
        historical.EndUtc = s.Clock.Now;
        await using (var db = s.Read())
        {
            (await db.Users.SingleAsync(x => x.Id == ineligible.Id)).IsEligible = false;
            (await db.Users.SingleAsync(x => x.Id == departed.Id)).DepartureVerifiedUtc = s.Clock.Now;
            (await db.Users.SingleAsync(x => x.Id == foreign.Id)).TenantId = Guid.NewGuid();
            (await db.EventMemberships.SingleAsync(x => x.UserId == removed.Id)).Status = MembershipStatus.Removed;
            db.Quests.Add(historical);
            db.QuestOwners.Add(new QuestOwner { QuestId = historical.Id, UserId = endedOnly.Id });
            await db.SaveChangesAsync();
            var states = await db.CalendarDeliveryStates.ToArrayAsync();
            Assert.Equal(new[] { (q1, attendee1.Id), (q2, attendee1.Id), (q2, attendee2.Id) }.OrderBy(x => x),
                states.Select(x => (x.QuestId, x.UserId)).OrderBy(x => x));
            Assert.All(states, x =>
            {
                Assert.Equal("REQUEST", x.IntendedMethod);
                Assert.Equal(x.IntendedSequence, x.SentSequence);
                Assert.True(x.MayHaveBeenDelivered);
            });
        }
        var before = (await s.OutboxAsync()).Select(x => x.Id).ToHashSet();
        var messageIndex = s.Gateway.Messages.Count;
        s.ActAs(s.Manager);
        await s.Events.ChangeStatusAsync(s.EventId, await s.EventVersionAsync(), EventStatus.Cancelled, "Weather closure");
        var rows = (await s.OutboxAsync()).Where(x => !before.Contains(x.Id)).ToArray();
        Assert.Equal(3, rows.Length);
        var changes = rows.Select(x => CoreCompositionScenario.Payload<ChangeEnvelope>(x.PayloadJson)).ToArray();
        var parent = Assert.Single(changes, x => x.QuestId is null);
        var parentRecipients = new[] { s.Manager, owner1, owner2, attendee1, attendee2, follower, invitee, registered };
        Assert.Equal(parentRecipients.Select(x => x.Id).Order(), parent.RecipientIds.Order());
        Assert.Equal(parent.RecipientIds.Length, parent.RecipientIds.Distinct().Count());
        Assert.Equal(0, parent.CalendarRevision);
        Assert.False(parent.CalendarChanged);
        Assert.All(changes, x =>
        {
            Assert.Equal(NotificationKind.EventCancelled, x.Kind);
            Assert.Equal(s.EventId, x.EventId);
            Assert.Equal(s.Manager.Id, x.ActorId);
            Assert.Equal(s.Clock.Now, x.OccurredUtc);
            Assert.Equal("Weather closure", x.Reason);
        });
        var attendeeSets = new Dictionary<Guid, UserAccount[]> { [q1] = [attendee1], [q2] = [attendee1, attendee2] };
        foreach (var (id, attendees) in attendeeSets)
        {
            var child = Assert.Single(changes, x => x.QuestId == id);
            Assert.Equal(attendees.Select(x => x.Id).Order(), child.RecipientIds.Order());
            Assert.Equal(attendees.Select(x => x.Id).Order(), child.PreviousAttendeeIds!.Order());
            Assert.Equal((id == q1 ? old1 : old2).CalendarRevision + 1, child.CalendarRevision);
            Assert.True(child.CalendarChanged);
            Assert.True(child.MaterialChange);
        }
        await AssertChangeIdentityAsync(s, rows);
        await s.FlushAsync();
        var delivered = s.Gateway.Messages.Skip(messageIndex).ToArray();
        var expectedKeys = parentRecipients.Select(x => $"change:{parent.ChangeId:N}:{x.Id:N}:email")
            .Concat(changes.Where(x => x.QuestId is not null).SelectMany(c =>
                attendeeSets[c.QuestId!.Value].Select(u => $"change:{c.ChangeId:N}:{u.Id:N}:email"))).Order();
        Assert.Equal(expectedKeys, delivered.Select(x => x.IdempotencyKey).Order());
        Assert.Equal(11, delivered.Length);
        foreach (var recipient in parentRecipients)
        {
            var message = Assert.Single(delivered, x => x.IdempotencyKey == $"change:{parent.ChangeId:N}:{recipient.Id:N}:email");
            AssertEmail(message, recipient.Email, "An Event was cancelled.");
            Assert.Null(message.CalendarContent);
            Assert.Null(message.CalendarMethod);
        }
        foreach (var (id, attendees) in attendeeSets)
        {
            var change = Assert.Single(changes, x => x.QuestId == id);
            foreach (var recipient in attendees)
            {
                var message = Assert.Single(delivered, x => x.IdempotencyKey == $"change:{change.ChangeId:N}:{recipient.Id:N}:email");
                AssertEmail(message, recipient.Email, "An Event was cancelled.");
                var old = id == q1 ? old1 : old2;
                AssertCalendar(message, id, change.CalendarRevision, "CANCEL", recipient.Email, s.Clock.Now, old.StartUtc, old.EndUtc);
            }
        }
        await using var after = s.Read();
        Assert.Equal(EventStatus.Cancelled, (await after.Events.SingleAsync()).Status);
        Assert.Equal(QuestStatus.Cancelled, (await s.QuestAsync(q1)).Status);
        Assert.Equal(QuestStatus.Cancelled, (await s.QuestAsync(q2)).Status);
        Assert.Equal(QuestStatus.Cancelled, (await s.QuestAsync(empty)).Status);
        Assert.Equal(QuestStatus.Cancelled, (await s.QuestAsync(draft)).Status);
        Assert.Equal(QuestStatus.Completed, (await s.QuestAsync(historical.Id)).Status);
        var actionIds = rows.Select(x => x.Id).ToArray();
        var parentHistory = Assert.Single(await after.EventStatusHistory.Where(x => x.Next == EventStatus.Cancelled).ToArrayAsync());
        Assert.Equal(EventStatus.Active, parentHistory.Previous);
        Assert.Equal(s.Manager.Id, parentHistory.ActorId);
        Assert.Equal("Weather closure", parentHistory.Reason);
        Assert.Equal(s.Clock.Now, parentHistory.OccurredUtc);
        foreach (var id in new[] { q1, q2 })
        {
            var history = Assert.Single(await after.QuestStatusHistory.Where(x => x.QuestId == id && x.Next == QuestStatus.Cancelled).ToArrayAsync());
            Assert.Equal(QuestStatus.Active, history.Previous);
            Assert.Equal(s.Manager.Id, history.ActorId);
            Assert.Equal("Weather closure", history.Reason);
            Assert.Equal(s.Clock.Now, history.OccurredUtc);
        }
        foreach (var change in changes)
        {
            var audit = await after.AuditEntries.SingleAsync(x => x.CorrelationId == change.ChangeId.ToString("N"));
            Assert.Equal(change.QuestId is null ? "Event.CancellationDelivery" : "Status:Active->Cancelled", audit.Action);
            Assert.Equal("Weather closure", audit.Reason);
        }
        var deliveries = await (from delivery in after.NotificationDeliveries
                                join notice in after.Notifications on delivery.NotificationId equals notice.Id
                                where actionIds.Contains(notice.SourceChangeId)
                                select delivery).ToArrayAsync();
        Assert.Equal(expectedKeys, deliveries.Select(x => x.DeduplicationKey).Order());
        Assert.All(deliveries, x =>
        {
            Assert.Equal(WorkStatus.Completed, x.Status);
            Assert.Equal("synthetic-provider-receipt", x.ProviderMessageId);
            Assert.Null(x.LeaseId);
        });
        foreach (var state in await after.CalendarDeliveryStates.ToArrayAsync())
        {
            Assert.Equal((state.QuestId == q1 ? old1 : old2).CalendarRevision + 1, state.SentSequence);
            Assert.Equal(state.IntendedSequence, state.SentSequence);
            Assert.Equal("CANCEL", state.IntendedMethod);
        }
        Assert.Null(await s.Queue.ClaimAsync("outbox"));
        Assert.Null(await s.Queue.ClaimAsync("delivery"));
        Assert.Equal(0, s.Directory.UserCalls);
        Assert.Equal(0, s.Directory.ExpansionCalls);
    }

    /// <summary>Distinguishes already-ended children from future withdrawals at the precise persisted end boundary.</summary>
    /// <param name="suspended">Whether the published child has first been suspended through real moderation.</param>
    /// <param name="ticksAfterEnd">Execution instant relative to the child's exclusive end.</param>
    /// <returns>A task completing after history, calendar revision and new-envelope assertions.</returns>
    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task CancelEvent_ChildEndBoundary_CompletesEndedChildrenWithoutNewWithdrawal(bool suspended, long ticksAfterEnd)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var attendee = await s.AddUserAsync("attendee");
        var id = await s.CreateQuestAsync(owner);
        await s.ParticipateAsync(id, attendee, ParticipationCommand.Join);
        s.ActAs(s.Manager);
        if (suspended)
            await s.Quests.ChangeStatusAsync(id, await s.QuestVersionAsync(id), QuestStatus.Suspended, "Safety review");
        await s.FlushAsync();
        var original = await s.QuestAsync(id);
        var baseline = (await s.OutboxAsync()).Select(x => x.Id).ToHashSet();
        s.Clock.Now = original.EndUtc.AddTicks(ticksAfterEnd);
        await s.Events.ChangeStatusAsync(s.EventId, await s.EventVersionAsync(), EventStatus.Cancelled, "Close event");
        var child = await s.QuestAsync(id);
        var ended = ticksAfterEnd >= 0;
        Assert.Equal(ended ? QuestStatus.Completed : QuestStatus.Cancelled, child.Status);
        Assert.Equal(original.CalendarRevision + (ended ? 0 : 1), child.CalendarRevision);
        var emitted = (await s.OutboxAsync()).Where(x => !baseline.Contains(x.Id))
            .Select(x => CoreCompositionScenario.Payload<ChangeEnvelope>(x.PayloadJson)).ToArray();
        Assert.Single(emitted, x => x.QuestId is null);
        Assert.Equal(ended ? 0 : 1, emitted.Count(x => x.QuestId == id));
        await using var db = s.Read();
        var history = Assert.Single(await db.QuestStatusHistory.Where(x => x.QuestId == id &&
            (x.Next == QuestStatus.Completed || x.Next == QuestStatus.Cancelled)).ToArrayAsync());
        Assert.Equal(ended ? null : s.Manager.Id, history.ActorId);
        Assert.Equal(ended ? "The Quest end time has been reached." : "Close event", history.Reason);
        Assert.Equal(s.Clock.Now, history.OccurredUtc);
        await s.FlushAsync();
    }

    /// <summary>Keeps never-published Event cancellation private even for an otherwise active member.</summary>
    /// <returns>A task completing after owner-only reads and absence of durable public intent.</returns>
    [Fact]
    public async Task CancelDraftEvent_HasNoPublicAudience_AndRemainsOwnerOnly()
    {
        await using var s = await CoreCompositionScenario.CreateAsync(publish: false);
        var member = await s.AddUserAsync("member", registered: true);
        await s.Events.ChangeStatusAsync(s.EventId, await s.EventVersionAsync(), EventStatus.Cancelled, "Draft abandoned");
        Assert.Empty(await s.OutboxAsync());
        Assert.Equal(EventStatus.Cancelled, (await s.Events.GetAsync(s.EventId)).Summary.Status);
        s.ActAs(member);
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Events.GetAsync(s.EventId));
        Assert.Equal(ErrorCode.NotFound, error.Code);
        await s.FlushAsync();
        Assert.Empty(s.Gateway.Messages);
    }

    /// <summary>Preserves direct Quest status audiences and separates suspension/reinstatement from parent cancellation.</summary>
    /// <param name="resume">Whether to resume a suspended Quest rather than cancel it directly.</param>
    /// <returns>A task completing after real mandatory status and calendar delivery assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModerateQuest_SuspendThenResumeOrCancel_KeepsChildStatusAudienceDistinct(bool resume)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var attendee = await s.AddUserAsync("attendee");
        var follower = await s.AddUserAsync("follower");
        var invitee = await s.AddUserAsync("invitee");
        var id = await s.CreateQuestAsync(owner, QuestVisibility.Private);
        foreach (var user in new[] { attendee, follower, invitee })
            await s.Quests.InviteAsync(id, user.Id);
        await s.ParticipateAsync(id, attendee, ParticipationCommand.Join);
        await s.ParticipateAsync(id, follower, ParticipationCommand.Follow);
        await s.FlushAsync();
        var revision = (await s.QuestAsync(id)).CalendarRevision;
        s.ActAs(s.Manager);
        var baseline = (await s.OutboxAsync()).Select(x => x.Id).ToHashSet();
        await s.Quests.ChangeStatusAsync(id, await s.QuestVersionAsync(id), QuestStatus.Suspended, "Moderator hold");
        var suspension = Assert.Single(await s.OutboxAsync(), x => !baseline.Contains(x.Id));
        var change = CoreCompositionScenario.Payload<ChangeEnvelope>(suspension.PayloadJson);
        Assert.Equal(NotificationKind.QuestSuspended, change.Kind);
        Assert.Equal(new[] { owner.Id, attendee.Id, follower.Id, invitee.Id }.Order(), change.RecipientIds.Order());
        var index = s.Gateway.Messages.Count;
        await s.FlushAsync();
        var cancel = Assert.Single(s.Gateway.Messages.Skip(index), x => x.CalendarMethod == "CANCEL");
        var quest = await s.QuestAsync(id);
        AssertCalendar(cancel, id, revision + 1, "CANCEL", attendee.Email, s.Clock.Now, quest.StartUtc, quest.EndUtc);
        baseline = (await s.OutboxAsync()).Select(x => x.Id).ToHashSet();
        index = s.Gateway.Messages.Count;
        s.ActAs(resume ? s.Manager : owner);
        await s.Quests.ChangeStatusAsync(id, await s.QuestVersionAsync(id),
            resume ? QuestStatus.Active : QuestStatus.Cancelled, "Resolution");
        var next = CoreCompositionScenario.Payload<ChangeEnvelope>(
            Assert.Single(await s.OutboxAsync(), x => !baseline.Contains(x.Id)).PayloadJson);
        Assert.Equal(resume ? NotificationKind.QuestReinstated : NotificationKind.QuestCancelled, next.Kind);
        Assert.Equal(id, next.QuestId);
        Assert.Equal(change.RecipientIds.Order(), next.RecipientIds.Order());
        Assert.Equal(revision + 2, next.CalendarRevision);
        await s.FlushAsync();
        var messages = s.Gateway.Messages.Skip(index).ToArray();
        Assert.Equal(new[] { owner.Email, attendee.Email, follower.Email, invitee.Email }.Order(),
            messages.Select(x => x.Recipient).Order());
        var calendar = Assert.Single(messages, x => x.CalendarContent is not null);
        AssertCalendar(calendar, id, revision + 2, resume ? "REQUEST" : "CANCEL", attendee.Email,
            s.Clock.Now, quest.StartUtc, quest.EndUtc);
        Assert.DoesNotContain((await s.OutboxAsync()).Select(x => CoreCompositionScenario.Payload<ChangeEnvelope>(x.PayloadJson)),
            x => x.Kind == NotificationKind.EventCancelled);
    }

    /// <summary>Leaves terminal history intact while cancelling a real private draft without disclosing it or creating participant intent.</summary>
    /// <returns>A task completing after precise historical-row, private-read and no-child-envelope checks.</returns>
    [Fact]
    public async Task CancelEvent_DraftAndTerminalChildren_PreservePrivateAndHistoricalSemantics()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var member = await s.AddUserAsync("member", registered: true);
        var draft = await s.CreateQuestAsync(owner, QuestVisibility.Private, publish: false);
        var completed = FoundationSeed.NewQuest(s.EventId, owner.Id);
        completed.Status = QuestStatus.Completed;
        var archived = FoundationSeed.NewQuest(s.EventId, owner.Id);
        archived.Status = QuestStatus.Archived;
        await using (var db = s.Read())
        {
            db.Quests.AddRange(completed, archived);
            await db.SaveChangesAsync();
        }
        var oldCompleted = await s.QuestAsync(completed.Id);
        var oldArchived = await s.QuestAsync(archived.Id);
        var oldDraft = await s.QuestAsync(draft);
        s.ActAs(s.Manager);
        await s.Events.ChangeStatusAsync(s.EventId, await s.EventVersionAsync(), EventStatus.Cancelled, "Draft work abandoned");
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(oldCompleted),
            System.Text.Json.JsonSerializer.Serialize(await s.QuestAsync(completed.Id)));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(oldArchived),
            System.Text.Json.JsonSerializer.Serialize(await s.QuestAsync(archived.Id)));
        var nowDraft = await s.QuestAsync(draft);
        Assert.Equal(QuestStatus.Cancelled, nowDraft.Status);
        Assert.Equal(oldDraft.CalendarRevision, nowDraft.CalendarRevision);
        var envelope = CoreCompositionScenario.Payload<ChangeEnvelope>(Assert.Single(await s.OutboxAsync()).PayloadJson);
        Assert.Null(envelope.QuestId);
        Assert.Equal(new[] { s.Manager.Id, member.Id }.Order(), envelope.RecipientIds.Order());
        s.ActAs(owner);
        Assert.True((await s.Quests.GetAsync(draft)).Summary.IsOwner);
        s.ActAs(member);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => s.Quests.GetAsync(draft))).Code);
        await s.FlushAsync();
        Assert.All(s.Gateway.Messages, x => Assert.Null(x.CalendarContent));
        await using var after = s.Read();
        var history = Assert.Single(await after.QuestStatusHistory.ToArrayAsync());
        Assert.Equal(draft, history.QuestId);
        Assert.Equal(QuestStatus.Draft, history.Previous);
        Assert.Equal(QuestStatus.Cancelled, history.Next);
        Assert.Equal(s.Manager.Id, history.ActorId);
        Assert.Equal("Draft work abandoned", history.Reason);
        Assert.Equal(s.Clock.Now, history.OccurredUtc);
    }

    /// <summary>Directly cancels an Active child without coalescing its owners, invitees and participants into a parent notice.</summary>
    /// <returns>A task completing after exact child audience and mandatory per-recipient output assertions.</returns>
    [Fact]
    public async Task CancelQuest_UnderActiveEvent_KeepsQuestStatusAudienceDistinct()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var attendee = await s.AddUserAsync("attendee");
        var follower = await s.AddUserAsync("follower");
        var invitee = await s.AddUserAsync("invitee");
        var id = await s.CreateQuestAsync(owner, QuestVisibility.Private);
        foreach (var user in new[] { attendee, follower, invitee })
            await s.Quests.InviteAsync(id, user.Id);
        await s.ParticipateAsync(id, attendee, ParticipationCommand.Join);
        await s.ParticipateAsync(id, follower, ParticipationCommand.Follow);
        await s.FlushAsync();
        var before = await s.QuestAsync(id);
        var baseline = (await s.OutboxAsync()).Select(x => x.Id).ToHashSet();
        var index = s.Gateway.Messages.Count;
        s.ActAs(owner);
        await s.Quests.ChangeStatusAsync(id, await s.QuestVersionAsync(id), QuestStatus.Cancelled, "Owner cancellation");
        var row = Assert.Single(await s.OutboxAsync(), x => !baseline.Contains(x.Id));
        var change = CoreCompositionScenario.Payload<ChangeEnvelope>(row.PayloadJson);
        Assert.Equal(NotificationKind.QuestCancelled, change.Kind);
        Assert.Equal(id, change.QuestId);
        Assert.Equal(new[] { owner.Id, attendee.Id, follower.Id, invitee.Id }.Order(), change.RecipientIds.Order());
        Assert.Equal(new[] { attendee.Id }, change.PreviousAttendeeIds);
        Assert.Equal(before.CalendarRevision + 1, change.CalendarRevision);
        await AssertChangeIdentityAsync(s, [row]);
        await s.FlushAsync();
        var messages = s.Gateway.Messages.Skip(index).ToArray();
        Assert.Equal(new[] { owner.Email, attendee.Email, follower.Email, invitee.Email }.Order(), messages.Select(x => x.Recipient).Order());
        foreach (var recipient in new[] { owner, attendee, follower, invitee })
            AssertEmail(Assert.Single(messages, x => x.Recipient == recipient.Email), recipient.Email, "A Quest was cancelled.");
        var cancellation = Assert.Single(messages, x => x.CalendarMethod == "CANCEL");
        AssertCalendar(cancellation, id, before.CalendarRevision + 1, "CANCEL", attendee.Email,
            s.Clock.Now, before.StartUtc, before.EndUtc);
        await using var db = s.Read();
        Assert.Equal(EventStatus.Active, (await db.Events.SingleAsync()).Status);
        Assert.Equal(QuestStatus.Cancelled, (await s.QuestAsync(id)).Status);
    }

    internal static async Task AssertChangeIdentityAsync(CoreCompositionScenario s, OutboxMessage[] rows)
    {
        await using var db = s.Read();
        foreach (var row in rows)
        {
            var change = CoreCompositionScenario.Payload<ChangeEnvelope>(row.PayloadJson);
            Assert.Equal(change.ChangeId, row.Id);
            Assert.Equal("sidequest.change.v1", row.Type);
            Assert.Equal(1, row.SchemaVersion);
            Assert.Equal(change.QuestId ?? change.EventId, row.AggregateId);
            Assert.Equal(change.ChangeId.ToString("N"), row.CorrelationId);
            Assert.Equal(change.OccurredUtc, row.OccurredUtc);
            Assert.Equal(change.OccurredUtc, row.DueUtc);
            var audit = Assert.Single(await db.AuditEntries.Where(x => x.CorrelationId == row.CorrelationId).ToArrayAsync());
            Assert.Equal(row.AggregateId, audit.ResourceId);
            Assert.Equal(change.ActorId, audit.ActorId);
            Assert.Equal(change.OccurredUtc, audit.OccurredUtc);
        }
    }

    /// <summary>Checks complete M3 default-branded bodies against literal business wording, independently of the production template engine.</summary>
    /// <param name="message">Actual single-recipient gateway submission.</param>
    /// <param name="recipient">Expected trusted destination.</param>
    /// <param name="summary">Literal safe business summary specified by the calling scenario.</param>
    internal static void AssertEmail(EmailMessage message, string recipient, string summary)
    {
        var text = "Sidequest\n" + summary +
            "\nCalendar clients may require acceptance of updates. Declining in Outlook does not change attendance; leave in Sidequest.";
        var html = "<p><strong>Sidequest</strong></p><p>" + summary +
            "</p><p>Calendar clients may require acceptance of updates. Declining in Outlook does not change attendance; leave in Sidequest.</p>";
        Assert.Equal(recipient, message.Recipient);
        Assert.Equal("Sidequest notification", message.Subject);
        Assert.Equal(text, message.TextBody);
        Assert.Equal(html, message.HtmlBody);
        Assert.Null(message.ReplyTo);
    }

    internal static void AssertCalendar(EmailMessage message, Guid questId, long sequence, string method,
        string recipient, DateTimeOffset stamp, DateTimeOffset start, DateTimeOffset end)
    {
        Assert.Equal(method, message.CalendarMethod);
        var calendar = Assert.IsType<string>(message.CalendarContent);
        Assert.DoesNotContain("\n", calendar.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.All(calendar.Split("\r\n"), line => Assert.InRange(Encoding.UTF8.GetByteCount(line), 0, 75));
        var lines = calendar.Replace("\r\n ", "", StringComparison.Ordinal).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines, x => x == "BEGIN:VEVENT");
        Assert.Contains($"UID:{questId:N}@sidequest.calendar", lines);
        Assert.Contains($"SEQUENCE:{sequence}", lines);
        Assert.Contains($"METHOD:{method}", lines);
        Assert.Contains($"STATUS:{(method == "CANCEL" ? "CANCELLED" : "CONFIRMED")}", lines);
        Assert.Contains($"DTSTAMP:{stamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}", lines);
        Assert.Contains($"DTSTART:{start.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}", lines);
        Assert.Contains($"DTEND:{end.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}", lines);
        Assert.Equal("ORGANIZER:mailto:organizer@example.invalid", Assert.Single(lines, x => x.StartsWith("ORGANIZER", StringComparison.Ordinal)));
        var attendee = Assert.Single(lines, x => x.StartsWith("ATTENDEE", StringComparison.Ordinal));
        Assert.EndsWith($":mailto:{recipient}", attendee);
        Assert.Contains("ROLE=REQ-PARTICIPANT", attendee);
        Assert.Contains($"RSVP={(method == "CANCEL" ? "FALSE" : "TRUE")}", attendee);
        if (method == "CANCEL")
        {
            Assert.Contains("SUMMARY:Sidequest appointment withdrawn", lines);
            Assert.DoesNotContain("sentinel", calendar);
        }
        else
            Assert.Contains("SUMMARY:Private title sentinel", lines);
    }
}
