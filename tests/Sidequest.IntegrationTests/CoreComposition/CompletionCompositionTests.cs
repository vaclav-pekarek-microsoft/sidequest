using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Quests;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Background;

namespace Sidequest.IntegrationTests.CoreComposition;

/// <summary>Exercises independent system reconciliation, immutable completion deadlines, queue ownership and durable idempotence.</summary>
public sealed class CompletionCompositionTests
{
    private static readonly DateTimeOffset EventEnd = new(2026, 7, 16, 22, 0, 0, TimeSpan.Zero);
    private const string EventReason = "The Event reached its inclusive local end date.";

    /// <summary>Commits shared Event/child completion before a later authorized Quest mutation is rejected.</summary>
    /// <param name="edit">Whether the rejected user command is an owner edit rather than an attendee join.</param>
    /// <returns>A task completing after fresh system-history, pending-request and unchanged-calendar assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedQuestMutation_AtEventExclusiveEnd_PersistsIndependentSystemCompletion(bool edit)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var attendee = await s.AddUserAsync("attendee");
        var requester = await s.AddUserAsync("requester", member: false);
        var active = await s.CreateQuestAsync(owner);
        var suspended = await s.CreateQuestAsync(owner);
        var draft = await s.CreateQuestAsync(owner, QuestVisibility.Private, publish: false);
        await s.ParticipateAsync(active, attendee, ParticipationCommand.Join);
        s.ActAs(s.Manager);
        await s.Quests.ChangeStatusAsync(suspended, await s.QuestVersionAsync(suspended), QuestStatus.Suspended, "Safety review hold");
        await s.FlushAsync();
        await using (var db = s.Read())
        {
            db.MembershipRequests.Add(new EventMembershipRequest
            {
                EventId = s.EventId, UserId = requester.Id, Status = MembershipRequestStatus.Pending, CreatedUtc = s.Clock.Now
            });
            db.EventInvitations.Add(new EventInvitation
            {
                EventId = s.EventId, UserId = requester.Id, InvitedById = s.Manager.Id,
                Status = EventInvitationStatus.Pending, CreatedUtc = s.Clock.Now, ExpiresUtc = EventEnd.AddHours(1)
            });
            await db.SaveChangesAsync();
        }
        var prior = new[] { await s.QuestAsync(active), await s.QuestAsync(suspended), await s.QuestAsync(draft) };
        var version = await s.QuestVersionAsync(active);
        var outbox = (await s.OutboxAsync()).Select(x => (x.Id, x.PayloadJson)).ToArray();
        var messages = s.Gateway.Messages.ToArray();
        string calendars;
        Guid[] auditIds;
        await using (var db = s.Read())
        {
            calendars = JsonSerializer.Serialize(await db.CalendarDeliveryStates.OrderBy(x => x.Id).ToArrayAsync());
            auditIds = await db.AuditEntries.Select(x => x.Id).ToArrayAsync();
        }
        s.Clock.Now = EventEnd;
        s.ActAs(edit ? owner : attendee);
        var error = await Assert.ThrowsAsync<DomainException>(() => edit
            ? s.Quests.EditAsync(active, version, CoreCompositionScenario.QuestInput() with { Title = "Rejected edit" })
            : s.Quests.ParticipateAsync(active, ParticipationCommand.Join));
        Assert.Equal(ErrorCode.Conflict, error.Code);
        Assert.Equal(edit ? "This item changed. Reload before saving." : "The Event is not active.", error.Message);
        await using var after = s.Read();
        Assert.Equal(EventStatus.Completed, (await after.Events.SingleAsync()).Status);
        var parentHistory = Assert.Single(await after.EventStatusHistory.Where(x => x.Next == EventStatus.Completed).ToArrayAsync());
        Assert.Null(parentHistory.ActorId);
        Assert.Equal(EventReason, parentHistory.Reason);
        Assert.Equal(EventEnd, parentHistory.OccurredUtc);
        foreach (var original in prior)
        {
            var child = await s.QuestAsync(original.Id);
            Assert.Equal(original.Status == QuestStatus.Draft ? QuestStatus.Cancelled : QuestStatus.Completed, child.Status);
            Assert.Equal(original.CalendarRevision, child.CalendarRevision);
            Assert.Equal(original.Title, child.Title);
            var history = Assert.Single(await after.QuestStatusHistory.Where(x => x.QuestId == original.Id &&
                (x.Next == QuestStatus.Completed || x.Next == QuestStatus.Cancelled)).ToArrayAsync());
            Assert.Null(history.ActorId);
            Assert.Equal("The parent Event has completed.", history.Reason);
            Assert.Equal(EventEnd, history.OccurredUtc);
        }
        var audits = await after.AuditEntries.Where(x => !auditIds.Contains(x.Id)).ToArrayAsync();
        Assert.Equal(new[] { "Event.Completed", "Status:Active->Completed", "Status:Draft->Cancelled", "Status:Suspended->Completed" }.Order(),
            audits.Select(x => x.Action).Order());
        Assert.All(audits, x => { Assert.Null(x.ActorId); Assert.Equal(EventEnd, x.OccurredUtc); });
        var request = await after.MembershipRequests.SingleAsync();
        Assert.Equal(MembershipRequestStatus.Rejected, request.Status);
        Assert.Null(request.DecidedById);
        Assert.Equal(EventReason, request.Reason);
        Assert.Equal(EventEnd, request.DecidedUtc);
        var invitation = await after.EventInvitations.SingleAsync();
        Assert.Equal(EventInvitationStatus.Expired, invitation.Status);
        Assert.Equal(EventEnd, invitation.ResolvedUtc);
        Assert.Equal(calendars, JsonSerializer.Serialize(await after.CalendarDeliveryStates.OrderBy(x => x.Id).ToArrayAsync()));
        Assert.Equal(outbox, (await s.OutboxAsync()).Select(x => (x.Id, x.PayloadJson)));
        Assert.Equal(messages, s.Gateway.Messages);
        Assert.Equal(ParticipationStatus.Joined, (await after.Participations.SingleAsync()).Status);
    }

    /// <summary>Retries a genuinely early Event job and later completes its real children using the immutable business deadline.</summary>
    /// <returns>A task completing after independently calculated backoff, payload, lease and exact completion-history checks.</returns>
    [Fact]
    public async Task EventCompletion_EarlyClaim_RetriesThenCompletesUsingImmutableCutoff()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var active = await s.CreateQuestAsync(owner);
        var draft = await s.CreateQuestAsync(owner, publish: false);
        await s.FlushAsync();
        var original = Assert.Single(await s.ScheduledAsync(WorkTypes.EventCompletion));
        var payload = CoreCompositionScenario.Payload<EventCompletionPayload>(original.PayloadJson);
        Assert.Equal(s.EventId, payload.EventId);
        Assert.Equal(1, payload.SchemaVersion);
        Assert.Equal(EventEnd, payload.ExpectedEndUtc);
        Assert.Equal($"event.complete.v1:{s.EventId:N}:{EventEnd.UtcTicks}", original.DeduplicationKey);
        var baseline = (await s.OutboxAsync()).Select(x => x.Id).ToArray();
        await s.SetDueAsync(original.Id, s.Clock.Now);
        s.Clock.Now = EventEnd.AddTicks(-1);
        Assert.True(await s.Runner.RunOnceAsync("scheduled"));
        var retry = await s.WorkAsync(original.Id);
        Assert.Equal(WorkStatus.Pending, retry.Status);
        Assert.Equal(1, retry.Attempts);
        var expectedDelay = TimeSpan.FromSeconds(5 + original.Id.ToByteArray()[0] % 11);
        Assert.Equal(expectedDelay, SqlWorkQueue.RetryDelay(1, original.Id));
        Assert.Equal(s.Clock.Now + expectedDelay, retry.DueUtc);
        Assert.Equal(original.PayloadJson, retry.PayloadJson);
        Assert.Equal("Retryable processing failure; inspect correlation-safe operational diagnostics.", retry.LastError);
        Assert.Null(retry.LeaseId);
        Assert.Null(retry.LeaseUntilUtc);
        Assert.Null(s.Execution.Lease);
        Assert.Equal(QuestStatus.Active, (await s.QuestAsync(active)).Status);
        await using (var db = s.Read())
            Assert.Empty(await db.EventStatusHistory.Where(x => x.Next == EventStatus.Completed).ToArrayAsync());
        s.Clock.Now = retry.DueUtc;
        Assert.True(s.Clock.Now > EventEnd);
        // Hold the earlier child claim without executing it so this retry, not another
        // handler's parent reconciliation, must perform the Event's business completion.
        var childWork = Assert.Single(await s.ScheduledAsync(WorkTypes.QuestCompletion));
        var childLease = Assert.IsType<WorkLease>(await s.Queue.ClaimAsync("scheduled"));
        Assert.Equal(childWork.Id, childLease.Id);
        Assert.True(await s.Runner.RunOnceAsync("scheduled"));
        var completed = await s.WorkAsync(original.Id);
        Assert.Equal(WorkStatus.Completed, completed.Status);
        Assert.Equal(2, completed.Attempts);
        Assert.Equal(retry.DueUtc, completed.DueUtc);
        Assert.Equal(original.PayloadJson, completed.PayloadJson);
        Assert.Null(completed.LeaseId);
        Assert.Null(completed.LeaseUntilUtc);
        Assert.Null(completed.LastError);
        Assert.Null(s.Execution.Lease);
        Assert.Equal(QuestStatus.Completed, (await s.QuestAsync(active)).Status);
        Assert.Equal(QuestStatus.Cancelled, (await s.QuestAsync(draft)).Status);
        await using var after = s.Read();
        var history = Assert.Single(await after.EventStatusHistory.Where(x => x.Next == EventStatus.Completed).ToArrayAsync());
        Assert.Equal(retry.DueUtc, history.OccurredUtc);
        Assert.Equal(EventReason, history.Reason);
        Assert.Null(history.ActorId);
        Assert.Equal(baseline, (await s.OutboxAsync()).Select(x => x.Id));
        await Assert.Single(s.Handlers, x => x.WorkType == WorkTypes.QuestCompletion)
            .ExecuteAsync(childLease.Id, CancellationToken.None);
        Assert.True(await s.Queue.CompleteAsync(childLease));
    }

    /// <summary>Does not apply an obsolete real job after a service edit changes its captured end deadline.</summary>
    /// <param name="parent">Whether the edited aggregate is the Event rather than the child Quest.</param>
    /// <returns>A task completing after obsolete-handler no-op and new-intent identity assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionIntent_AfterRealEndEdit_DoesNotApplyOldDeadline(bool parent)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var id = await s.CreateQuestAsync(owner);
        await s.FlushAsync();
        var type = parent ? WorkTypes.EventCompletion : WorkTypes.QuestCompletion;
        var old = Assert.Single(await s.ScheduledAsync(type));
        if (parent)
        {
            s.ActAs(s.Manager);
            await s.Events.EditAsync(s.EventId, await s.EventVersionAsync(), CoreCompositionScenario.EventInput(17));
        }
        else
        {
            s.ActAs(owner);
            await s.Quests.EditAsync(id, await s.QuestVersionAsync(id), CoreCompositionScenario.QuestInput(endHour: 16));
        }
        var updated = Assert.Single(await s.ScheduledAsync(type), x => x.Id != old.Id);
        Assert.NotEqual(old.DeduplicationKey, updated.DeduplicationKey);
        Assert.Equal(parent ? EventEnd.AddDays(1) : old.DueUtc.AddHours(1), updated.DueUtc);
        s.Clock.Now = old.DueUtc;
        await Assert.Single(s.Handlers, x => x.WorkType == type).ExecuteAsync(old.Id, CancellationToken.None);
        Assert.Equal(old.PayloadJson, (await s.WorkAsync(old.Id)).PayloadJson);
        await using var db = s.Read();
        Assert.Equal(EventStatus.Active, (await db.Events.SingleAsync()).Status);
        if (!parent)
            Assert.Equal(QuestStatus.Active, (await s.QuestAsync(id)).Status);
        Assert.Empty(await db.EventStatusHistory.Where(x => x.Next == EventStatus.Completed).ToArrayAsync());
        Assert.Empty(await db.QuestStatusHistory.Where(x => x.Next == QuestStatus.Completed).ToArrayAsync());
        Assert.Equal(WorkStatus.Pending, (await s.WorkAsync(updated.Id)).Status);
    }

    /// <summary>Still reconciles the real overdue parent even when the child job's end snapshot is obsolete.</summary>
    /// <returns>A task completing after exact parent and child system-history assertions.</returns>
    [Fact]
    public async Task StaleQuestCompletion_StillReconcilesOverdueParent()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var id = await s.CreateQuestAsync(owner);
        var old = Assert.Single(await s.ScheduledAsync(WorkTypes.QuestCompletion));
        await s.Quests.EditAsync(id, await s.QuestVersionAsync(id), CoreCompositionScenario.QuestInput(endHour: 16));
        s.Clock.Now = EventEnd;
        await Assert.Single(s.Handlers, x => x.WorkType == WorkTypes.QuestCompletion).ExecuteAsync(old.Id, CancellationToken.None);
        Assert.Equal(QuestStatus.Completed, (await s.QuestAsync(id)).Status);
        await using var db = s.Read();
        Assert.Equal(EventStatus.Completed, (await db.Events.SingleAsync()).Status);
        var history = Assert.Single(await db.QuestStatusHistory.Where(x => x.Next == QuestStatus.Completed).ToArrayAsync());
        Assert.Null(history.ActorId);
        Assert.Equal("The parent Event has completed.", history.Reason);
        Assert.Equal(EventEnd, history.OccurredUtc);
        Assert.Equal(old.PayloadJson, (await s.WorkAsync(old.Id)).PayloadJson);
    }

    /// <summary>Proves completion handlers never mutate claimed lease fields, and repeated execution cannot repeat history or use stale queue tokens.</summary>
    /// <param name="parent">Whether to execute the Event handler instead of the Quest handler.</param>
    /// <returns>A task completing after handler/queue separation and replay-idempotence checks.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionHandlers_LeaveClaimedFieldsToQueue_AndRepeatedCompletionRejectsStaleTokens(bool parent)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var questId = await s.CreateQuestAsync(owner);
        var attendee = await s.AddUserAsync("attendee");
        s.ActAs(attendee);
        await s.Notifications.SavePreferencesAsync(new(false, false, false, 0.5m, null));
        await s.Quests.ParticipateAsync(questId, ParticipationCommand.Join);
        await s.FlushAsync();
        var type = parent ? WorkTypes.EventCompletion : WorkTypes.QuestCompletion;
        var work = Assert.Single(await s.ScheduledAsync(type));
        await s.SetDueAsync(work.Id, s.Clock.Now);
        var failedLease = Assert.IsType<WorkLease>(await s.Queue.ClaimAsync("scheduled"));
        Assert.Equal(work.Id, failedLease.Id);
        Assert.True(await s.Queue.FailAsync(failedLease, new InvalidOperationException("Retry ownership control")));
        var originalQuest = await s.QuestAsync(questId);
        var originalOutbox = (await s.OutboxAsync()).Select(x => (x.Id, x.PayloadJson)).ToArray();
        var originalMessages = s.Gateway.Messages.ToArray();
        string originalCalendar;
        await using (var db = s.Read())
            originalCalendar = JsonSerializer.Serialize(await db.CalendarDeliveryStates.OrderBy(x => x.Id).ToArrayAsync());
        s.Clock.Now = work.DueUtc;
        var lease = Assert.IsType<WorkLease>(await s.Queue.ClaimAsync("scheduled"));
        Assert.Equal(work.Id, lease.Id);
        var before = await s.WorkAsync(work.Id);
        Assert.Equal(WorkStatus.Processing, before.Status);
        Assert.Equal(2, before.Attempts);
        Assert.Equal("Retryable processing failure; inspect correlation-safe operational diagnostics.", before.LastError);
        Assert.Equal(lease.Token, before.LeaseId);
        Assert.Equal(s.Clock.Now + s.Options.LeaseDuration, before.LeaseUntilUtc);
        var handler = Assert.Single(s.Handlers, x => x.WorkType == type);
        await handler.ExecuteAsync(work.Id, CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await s.WorkAsync(work.Id)));
        await using (var db = s.Read())
        {
            if (parent)
                Assert.Equal(EventStatus.Completed, (await db.Events.SingleAsync()).Status);
            else
                Assert.Equal(QuestStatus.Completed, (await s.QuestAsync(questId)).Status);
        }
        Assert.True(await s.Queue.CompleteAsync(lease));
        string history;
        await using (var db = s.Read())
            history = JsonSerializer.Serialize(new
            {
                Audit = await db.AuditEntries.OrderBy(x => x.Id).ToArrayAsync(),
                EventHistory = await db.EventStatusHistory.OrderBy(x => x.Id).ToArrayAsync(),
                QuestHistory = await db.QuestStatusHistory.OrderBy(x => x.Id).ToArrayAsync()
            });
        await handler.ExecuteAsync(work.Id, CancellationToken.None);
        Assert.False(await s.Queue.CompleteAsync(lease));
        Assert.False(await s.Queue.FailAsync(lease, new InvalidOperationException("Stale claimant")));
        await using var after = s.Read();
        Assert.Equal(history, JsonSerializer.Serialize(new
        {
            Audit = await after.AuditEntries.OrderBy(x => x.Id).ToArrayAsync(),
            EventHistory = await after.EventStatusHistory.OrderBy(x => x.Id).ToArrayAsync(),
            QuestHistory = await after.QuestStatusHistory.OrderBy(x => x.Id).ToArrayAsync()
        }));
        var completed = await s.WorkAsync(work.Id);
        Assert.Equal(WorkStatus.Completed, completed.Status);
        Assert.Equal(2, completed.Attempts);
        Assert.Null(completed.LastError);
        Assert.Null(completed.LeaseId);
        Assert.Equal(work.PayloadJson, completed.PayloadJson);
        Assert.Equal(originalQuest.CalendarRevision, (await s.QuestAsync(questId)).CalendarRevision);
        Assert.Equal(originalOutbox, (await s.OutboxAsync()).Select(x => (x.Id, x.PayloadJson)));
        Assert.Equal(originalMessages, s.Gateway.Messages);
        Assert.Equal(originalCalendar, JsonSerializer.Serialize(await after.CalendarDeliveryStates.OrderBy(x => x.Id).ToArrayAsync()));
    }

    /// <summary>Requires durable eventual completion at the original child cutoff after a forced early claim, not merely absence of premature completion.</summary>
    /// <returns>A task completing only when the original intent still produces completion at its immutable deadline.</returns>
    [Fact]
    public async Task EarlyQuestCompletion_MustNotLoseDurableCompletionAtImmutableCutoff()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var id = await s.CreateQuestAsync(owner);
        await s.FlushAsync();
        var work = Assert.Single(await s.ScheduledAsync(WorkTypes.QuestCompletion));
        var intent = CoreCompositionScenario.Payload<QuestCompletionPayload>(work.PayloadJson);
        Assert.Equal(id, intent.QuestId);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 13, 0, 0, TimeSpan.Zero), intent.EndUtc);
        Assert.Equal(intent.EndUtc, work.DueUtc);
        await s.SetDueAsync(work.Id, s.Clock.Now);
        Assert.True(await s.Runner.RunOnceAsync("scheduled"));
        Assert.Equal(QuestStatus.Active, (await s.QuestAsync(id)).Status);
        var earlyAttempt = await s.WorkAsync(work.Id);
        Assert.Equal(1, earlyAttempt.Attempts);
        Assert.Equal(work.PayloadJson, earlyAttempt.PayloadJson);
        Assert.Null(earlyAttempt.LeaseId);
        Assert.Null(earlyAttempt.LeaseUntilUtc);
        Assert.Null(s.Execution.Lease);
        // No synthetic replacement job, user mutation or direct handler call can rescue lost durable intent.
        s.Clock.Now = intent.EndUtc;
        await s.DrainAsync("scheduled");
        var after = await s.QuestAsync(id);
        Assert.Equal(QuestStatus.Completed, after.Status);
        Assert.Equal(WorkStatus.Completed, (await s.WorkAsync(work.Id)).Status);
        await using var db = s.Read();
        var history = Assert.Single(await db.QuestStatusHistory.Where(x => x.Next == QuestStatus.Completed).ToArrayAsync());
        Assert.Equal(intent.EndUtc, history.OccurredUtc);
        Assert.Equal(QuestStatus.Active, history.Previous);
        Assert.Equal("The Quest end time has been reached.", history.Reason);
        Assert.Null(history.ActorId);
    }

    /// <summary>Leaves repeated early Event failures under queue control through the last retryable and first dead-letter attempt.</summary>
    /// <returns>A task completing after all eight exact retry intervals, immutable intent and absent business effects are verified.</returns>
    [Fact]
    public async Task EventCompletion_EarlyRetries_ReachQueueDeadLetterAtEighthAttempt_WithoutBusinessEffects()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var id = await s.CreateQuestAsync(owner, QuestVisibility.Private);
        var work = Assert.Single(await s.ScheduledAsync(WorkTypes.EventCompletion));
        await s.SetDueAsync(work.Id, s.Clock.Now);
        var jitter = work.Id.ToByteArray()[0] % 11;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var claimedAt = s.Clock.Now;
            Assert.True(await s.Runner.RunOnceAsync("scheduled"));
            var failed = await s.WorkAsync(work.Id);
            Assert.Equal(attempt == 8 ? WorkStatus.DeadLetter : WorkStatus.Pending, failed.Status);
            Assert.Equal(attempt, failed.Attempts);
            var delaySeconds = (5 << (attempt - 1)) + jitter;
            Assert.Equal(claimedAt.AddSeconds(delaySeconds), failed.DueUtc);
            Assert.Equal(work.PayloadJson, failed.PayloadJson);
            Assert.Equal(work.DeduplicationKey, failed.DeduplicationKey);
            Assert.Equal("Retryable processing failure; inspect correlation-safe operational diagnostics.", failed.LastError);
            Assert.Null(failed.LeaseId);
            Assert.Null(failed.LeaseUntilUtc);
            Assert.Null(s.Execution.Lease);
            s.Clock.Now = failed.DueUtc;
        }
        var terminal = await s.WorkAsync(work.Id);
        Assert.False(await s.Runner.RunOnceAsync("scheduled"));
        Assert.Equal(JsonSerializer.Serialize(terminal), JsonSerializer.Serialize(await s.WorkAsync(work.Id)));
        Assert.Equal(QuestStatus.Active, (await s.QuestAsync(id)).Status);
        await using var db = s.Read();
        Assert.Equal(EventStatus.Active, (await db.Events.SingleAsync()).Status);
        Assert.Empty(await db.EventStatusHistory.Where(x => x.Next == EventStatus.Completed).ToArrayAsync());
        Assert.Empty(await db.QuestStatusHistory.Where(x => x.Next == QuestStatus.Completed).ToArrayAsync());
        Assert.Empty(await s.OutboxAsync());
        Assert.Empty(s.Gateway.Messages);
    }
}
