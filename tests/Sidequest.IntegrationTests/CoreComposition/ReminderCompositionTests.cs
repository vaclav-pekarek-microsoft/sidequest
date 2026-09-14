using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Background;

namespace Sidequest.IntegrationTests.CoreComposition;

/// <summary>Tests real participation/preference producers, outbox scheduling, immutable reminder timing and both delivery eligibility gates.</summary>
public sealed class ReminderCompositionTests
{
    /// <summary>Follows without calendar intent, then joins and schedules exactly one target reminder through the real Joined outbox.</summary>
    /// <returns>A task completing after participation audit, explicit target, calendar and reminder payload assertions.</returns>
    [Fact]
    public async Task FollowThenJoin_OnlyJoinedTargetGetsProductionReminderIntent()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var target = await s.AddUserAsync("target");
        var follower = await s.AddUserAsync("follower");
        var id = await s.CreateQuestAsync(owner);
        await s.FlushAsync();
        var baseline = (await s.OutboxAsync()).Select(x => x.Id).ToArray();
        var revision = (await s.QuestAsync(id)).CalendarRevision;
        await s.ParticipateAsync(id, target, ParticipationCommand.Follow);
        await s.ParticipateAsync(id, follower, ParticipationCommand.Follow);
        Assert.Equal(baseline, (await s.OutboxAsync()).Select(x => x.Id));
        Assert.Empty(await s.ScheduledAsync(WorkTypes.Reminder));
        Assert.Equal(revision, (await s.QuestAsync(id)).CalendarRevision);
        await using (var db = s.Read())
        {
            Assert.Empty(await db.CalendarDeliveryStates.ToArrayAsync());
            Assert.Equal(new[] { target.Id, follower.Id }.Order(),
                (await db.Participations.Where(x => x.Status == ParticipationStatus.Following).Select(x => x.UserId).ToArrayAsync()).Order());
            var audit = Assert.Single(await db.AuditEntries.Where(x => x.Action == $"Participation:{target.Id:N}:None->Following").ToArrayAsync());
            Assert.Equal(target.Id, audit.ActorId);
            Assert.Equal(s.Clock.Now, audit.OccurredUtc);
        }
        s.ActAs(target);
        await s.Notifications.SavePreferencesAsync(new(false, false, true, 0.5m, null));
        await s.Quests.ParticipateAsync(id, ParticipationCommand.Join);
        var row = Assert.Single(await s.OutboxAsync(), x => !baseline.Contains(x.Id));
        var change = CoreCompositionScenario.Payload<ChangeEnvelope>(row.PayloadJson);
        Assert.Equal(NotificationKind.Joined, change.Kind);
        Assert.Equal(new[] { target.Id }, change.AffectedUserIds);
        Assert.Equal(new[] { owner.Id, target.Id }.Order(), change.RecipientIds.Order());
        Assert.Equal(revision + 1, change.CalendarRevision);
        Assert.True(change.CalendarChanged);
        Assert.Empty(await s.ScheduledAsync(WorkTypes.Reminder));
        await EventCancellationCompositionTests.AssertChangeIdentityAsync(s, [row]);
        var messageIndex = s.Gateway.Messages.Count;
        await s.FlushAsync();
        var work = Assert.Single(await s.ScheduledAsync(WorkTypes.Reminder));
        var quest = await s.QuestAsync(id);
        AssertReminder(work, id, target.Id, quest.StartRevision, quest.StartUtc, quest.StartUtc.AddMinutes(-30), 0.5m);
        var message = Assert.Single(s.Gateway.Messages.Skip(messageIndex));
        EventCancellationCompositionTests.AssertEmail(message, target.Email,
            "Quest attendance changed. Joining requires service and calendar messages.");
        EventCancellationCompositionTests.AssertCalendar(message, id, revision + 1, "REQUEST",
            target.Email, s.Clock.Now, quest.StartUtc, quest.EndUtc);
    }

    /// <summary>Uses decimal leads with exact before/inside/at/after-start boundaries and does not duplicate unchanged preferences.</summary>
    /// <param name="minutesBeforeStart">Distance from execution to the scheduled Quest start.</param>
    /// <returns>A task completing after exact current reminder intent or its deliberate absence.</returns>
    [Theory]
    [InlineData(60)]
    [InlineData(20)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task JoinedReminder_UsesDecimalLeadAndStartBoundary(int minutesBeforeStart)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var target = await s.AddUserAsync("target");
        var id = await s.CreateQuestAsync(owner);
        var quest = await s.QuestAsync(id);
        s.Clock.Now = quest.StartUtc.AddMinutes(-minutesBeforeStart);
        s.ActAs(target);
        await s.Notifications.SavePreferencesAsync(new(false, false, true, 0.5m, null));
        await s.Quests.ParticipateAsync(id, ParticipationCommand.Join);
        await s.FlushAsync();
        var rows = await s.ScheduledAsync(WorkTypes.Reminder);
        if (minutesBeforeStart <= 0)
            Assert.Empty(rows);
        else
        {
            var work = Assert.Single(rows);
            var due = minutesBeforeStart < 30 ? s.Clock.Now : quest.StartUtc.AddMinutes(-30);
            AssertReminder(work, id, target.Id, quest.StartRevision, quest.StartUtc, due, 0.5m);
            await s.Notifications.SavePreferencesAsync(new(false, false, true, 0.5m, null));
            Assert.Equal(JsonSerializer.Serialize(work), JsonSerializer.Serialize(Assert.Single(await s.ScheduledAsync(WorkTypes.Reminder))));
        }
        Assert.Equal(ParticipationStatus.Joined, (await s.Quests.GetAsync(id)).Summary.Participation);
    }

    /// <summary>Enforces exact lead-time range and precision without corrupting existing producer-scheduled intent on invalid input.</summary>
    /// <param name="hours">Requested decimal lead time.</param>
    /// <param name="valid">Whether the inclusive range and two-decimal precision permit this value.</param>
    /// <returns>A task completing after preserved or precisely replaced scheduling assertions.</returns>
    [Theory]
    [InlineData("0.009", false)]
    [InlineData("0.01", true)]
    [InlineData("0.011", false)]
    [InlineData("168", true)]
    [InlineData("168.01", false)]
    public async Task SaveReminderLead_ValidatesExactBounds_WithoutChangingExistingIntent(string hours, bool valid)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var (_, target, _, id, work) = await PrepareAsync(s);
        var lead = decimal.Parse(hours, System.Globalization.CultureInfo.InvariantCulture);
        s.ActAs(target);
        if (valid)
        {
            await s.Notifications.SavePreferencesAsync(new(false, false, true, lead, null));
            var quest = await s.QuestAsync(id);
            var due = lead == 168m ? s.Clock.Now : quest.StartUtc.AddSeconds(-36);
            AssertReminder(await s.WorkAsync(work.Id), id, target.Id, quest.StartRevision, quest.StartUtc, due, lead);
            Assert.Equal(lead, (await s.Notifications.GetPreferencesAsync()).ReminderHours);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                s.Notifications.SavePreferencesAsync(new(false, false, true, lead, null)));
            Assert.Equal(ErrorCode.Validation, error.Code);
            Assert.Equal("ReminderHours", error.Field);
            Assert.Equal("Reminder hours must be 0.01–168 with at most two decimal places.", error.Message);
            Assert.Equal(JsonSerializer.Serialize(work), JsonSerializer.Serialize(await s.WorkAsync(work.Id)));
            Assert.Equal(0.5m, (await s.Notifications.GetPreferencesAsync()).ReminderHours);
        }
    }

    /// <summary>Replaces failed pending intent, invalidates a live claim when disabled, then never replays a delivered start revision.</summary>
    /// <returns>A task completing after exact key/payload, lease-reset, both-channel suppression and actual-send assertions.</returns>
    [Fact]
    public async Task SavePreferences_ReplacesPendingIntent_DisablesBothChannels_AndDoesNotReplayDeliveredRevision()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var (_, target, _, id, work) = await PrepareAsync(s);
        s.Clock.Now = work.DueUtc;
        var firstLease = Assert.IsType<WorkLease>(await s.Queue.ClaimAsync("scheduled"));
        Assert.Equal(work.Id, firstLease.Id);
        Assert.True(await s.Queue.FailAsync(firstLease, new InvalidOperationException("Transient control failure")));
        var failed = await s.WorkAsync(work.Id);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal(WorkStatus.Pending, failed.Status);
        Assert.Equal("Retryable processing failure; inspect correlation-safe operational diagnostics.", failed.LastError);
        s.ActAs(target);
        await s.Notifications.SavePreferencesAsync(new(false, false, true, 0.25m, null));
        var replacement = await s.WorkAsync(work.Id);
        var quest = await s.QuestAsync(id);
        AssertReminder(replacement, id, target.Id, quest.StartRevision, quest.StartUtc, quest.StartUtc.AddMinutes(-15), 0.25m);
        Assert.Equal(work.DeduplicationKey, replacement.DeduplicationKey);
        Assert.Equal(0, replacement.Attempts);
        Assert.Null(replacement.LastError);
        Assert.Null(replacement.LeaseId);
        Assert.Null(replacement.LeaseUntilUtc);
        s.Clock.Now = replacement.DueUtc;
        var liveLease = Assert.IsType<WorkLease>(await s.Queue.ClaimAsync("scheduled"));
        Assert.Equal(work.Id, liveLease.Id);
        await s.Notifications.SavePreferencesAsync(new(false, false, false, 0.25m, null));
        var disabled = await s.WorkAsync(work.Id);
        Assert.Equal(WorkStatus.Superseded, disabled.Status);
        Assert.Null(disabled.LeaseId);
        Assert.Null(disabled.LeaseUntilUtc);
        s.Execution.Lease = liveLease;
        try
        {
            await Assert.Single(s.Handlers, x => x.WorkType == WorkTypes.Reminder).ExecuteAsync(work.Id, CancellationToken.None);
        }
        finally
        {
            s.Execution.Lease = null;
        }
        Assert.False(await s.Queue.CompleteAsync(liveLease));
        await using (var db = s.Read())
        {
            Assert.Empty(await db.Notifications.Where(x => x.SourceChangeId == work.Id).ToArrayAsync());
            Assert.Empty(await db.NotificationDeliveries.Where(x => x.DeduplicationKey ==
                $"reminder:{id:N}:{target.Id:N}:{quest.StartRevision}:email").ToArrayAsync());
        }
        var index = s.Gateway.Messages.Count;
        await s.Notifications.SavePreferencesAsync(new(false, false, true, 0.25m, null));
        Assert.True(await s.Runner.RunOnceAsync("scheduled"));
        await s.DrainAsync("delivery");
        var message = Assert.Single(s.Gateway.Messages.Skip(index));
        EventCancellationCompositionTests.AssertEmail(message, target.Email, "Your joined Quest starts soon. Check Sidequest for current details.");
        Assert.Null(message.CalendarContent);
        await s.Notifications.SavePreferencesAsync(new(false, false, false, 0.25m, null));
        await s.Notifications.SavePreferencesAsync(new(false, false, true, 0.5m, null));
        Assert.Equal(WorkStatus.Completed, (await s.WorkAsync(work.Id)).Status);
        Assert.Single(await s.ScheduledAsync(WorkTypes.Reminder));
        Assert.False(await s.Runner.RunOnceAsync("scheduled"));
        Assert.Equal(index + 1, s.Gateway.Messages.Count);
    }

    /// <summary>Separates calendar-only edits from start changes and lets real QuestUpdated outbox processing replace only the joined recipient's revision.</summary>
    /// <returns>A task completing after old-lease invalidation and exact new reminder payload assertions.</returns>
    [Fact]
    public async Task EditQuestStart_SupersedesOldRevision_AndSchedulesOnlyJoinedRecipient()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var (owner, target, follower, id, old) = await PrepareAsync(s);
        var before = await s.QuestAsync(id);
        s.ActAs(owner);
        await s.Quests.EditAsync(id, await s.QuestVersionAsync(id),
            CoreCompositionScenario.QuestInput(QuestVisibility.Private, location: "Moved room sentinel"));
        await s.FlushAsync();
        var locationEdit = await s.QuestAsync(id);
        Assert.Equal(before.CalendarRevision + 1, locationEdit.CalendarRevision);
        Assert.Equal(before.StartRevision, locationEdit.StartRevision);
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(await s.WorkAsync(old.Id)));
        s.Clock.Now = old.DueUtc;
        var lease = Assert.IsType<WorkLease>(await s.Queue.ClaimAsync("scheduled"));
        Assert.Equal(old.Id, lease.Id);
        var baseline = (await s.OutboxAsync()).Select(x => x.Id).ToHashSet();
        await s.Quests.EditAsync(id, await s.QuestVersionAsync(id), CoreCompositionScenario.QuestInput(QuestVisibility.Private, startHour: 14));
        var changed = await s.QuestAsync(id);
        Assert.Equal(before.StartRevision + 1, changed.StartRevision);
        Assert.Equal(locationEdit.CalendarRevision + 1, changed.CalendarRevision);
        var row = Assert.Single(await s.OutboxAsync(), x => !baseline.Contains(x.Id));
        Assert.Equal(NotificationKind.QuestUpdated, CoreCompositionScenario.Payload<ChangeEnvelope>(row.PayloadJson).Kind);
        Assert.Equal(WorkStatus.Processing, (await s.WorkAsync(old.Id)).Status);
        await s.DrainAsync("outbox");
        var superseded = await s.WorkAsync(old.Id);
        Assert.Equal(WorkStatus.Superseded, superseded.Status);
        Assert.Null(superseded.LeaseId);
        Assert.Null(superseded.LeaseUntilUtc);
        Assert.Equal(old.PayloadJson, superseded.PayloadJson);
        var current = Assert.Single(await s.ScheduledAsync(WorkTypes.Reminder), x => x.Id != old.Id);
        AssertReminder(current, id, target.Id, before.StartRevision + 1, changed.StartUtc, changed.StartUtc.AddMinutes(-30), 0.5m);
        Assert.DoesNotContain(await s.ScheduledAsync(WorkTypes.Reminder), x => x.UserId == follower.Id);
        Assert.False(await s.Queue.CompleteAsync(lease));
        await s.DrainAsync("delivery");
    }

    /// <summary>Runs the scheduled reminder at its exact instant through the real handler and dispatcher with durable idempotent outcomes.</summary>
    /// <returns>A task completing after exact source/key/recipient, provider receipt and no-calendar assertions.</returns>
    [Fact]
    public async Task Reminder_AtScheduledInstant_TraversesRunnerAndRealSend_ExactlyOneDurableOutcome()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var (_, target, _, id, work) = await PrepareAsync(s);
        s.Clock.Now = work.DueUtc;
        var index = s.Gateway.Messages.Count;
        Assert.True(await s.Runner.RunOnceAsync("scheduled"));
        var completed = await s.WorkAsync(work.Id);
        Assert.Equal(WorkStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Attempts);
        Assert.Equal(work.PayloadJson, completed.PayloadJson);
        Assert.Null(completed.LeaseId);
        Assert.Null(s.Execution.Lease);
        await s.DrainAsync("delivery");
        var message = Assert.Single(s.Gateway.Messages.Skip(index));
        var revision = (await s.QuestAsync(id)).StartRevision;
        Assert.Equal($"reminder:{id:N}:{target.Id:N}:{revision}:email", message.IdempotencyKey);
        EventCancellationCompositionTests.AssertEmail(message, target.Email, "Your joined Quest starts soon. Check Sidequest for current details.");
        Assert.Null(message.CalendarContent);
        Assert.Null(message.CalendarMethod);
        await using var db = s.Read();
        var notification = Assert.Single(await db.Notifications.Where(x => x.SourceChangeId == work.Id).ToArrayAsync());
        Assert.Equal(NotificationKind.Reminder, notification.Kind);
        Assert.Equal(target.Id, notification.UserId);
        Assert.Equal(id, notification.QuestId);
        Assert.Equal(s.EventId, notification.EventId);
        Assert.Equal(work.DueUtc, notification.CreatedUtc);
        var delivery = Assert.Single(await db.NotificationDeliveries.Where(x => x.NotificationId == notification.Id).ToArrayAsync());
        Assert.Equal(message.IdempotencyKey, delivery.DeduplicationKey);
        Assert.Equal(WorkStatus.Completed, delivery.Status);
        Assert.Equal("synthetic-provider-receipt", delivery.ProviderMessageId);
        Assert.Equal(1, delivery.Attempts);
        Assert.Null(delivery.LeaseId);
        Assert.False(await s.Runner.RunOnceAsync("scheduled"));
        Assert.False(await s.Runner.RunOnceAsync("delivery"));
        Assert.Equal(index + 1, s.Gateway.Messages.Count);
    }

    /// <summary>Invokes a genuinely claimed reminder after each distinct eligibility gate changes, instead of mistaking an unclaimable row for handler evidence.</summary>
    /// <param name="gate">The real producer or account control that invalidates this claim.</param>
    /// <returns>A task completing after zero source-specific notification/delivery and precise lease-outcome checks.</returns>
    [Theory]
    [InlineData("start")]
    [InlineData("leave")]
    [InlineData("removal")]
    [InlineData("preferences")]
    [InlineData("event")]
    [InlineData("quest")]
    [InlineData("departed")]
    [InlineData("ineligible")]
    [InlineData("late")]
    public async Task ReminderHandler_RejectsStaleOrIneligibleIntent_WithoutNotificationOrDelivery(string gate)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var (owner, target, _, id, work) = await PrepareAsync(s);
        s.Clock.Now = gate == "late" ? work.DueUtc.AddMinutes(2).AddTicks(1) : work.DueUtc;
        var lease = Assert.IsType<WorkLease>(await s.Queue.ClaimAsync("scheduled"));
        Assert.Equal(work.Id, lease.Id);
        await InvalidateAsync(s, owner, target, id, gate);
        var before = await s.WorkAsync(work.Id);
        Assert.Equal(gate == "preferences" ? WorkStatus.Superseded : WorkStatus.Processing, before.Status);
        s.Execution.Lease = lease;
        try
        {
            await Assert.Single(s.Handlers, x => x.WorkType == WorkTypes.Reminder).ExecuteAsync(work.Id, CancellationToken.None);
        }
        finally
        {
            s.Execution.Lease = null;
        }
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await s.WorkAsync(work.Id)));
        await using var db = s.Read();
        Assert.Empty(await db.Notifications.Where(x => x.SourceChangeId == work.Id).ToArrayAsync());
        Assert.Empty(await db.NotificationDeliveries.Where(x => x.DeduplicationKey.StartsWith($"reminder:{id:N}:")).ToArrayAsync());
        Assert.Equal(gate != "preferences", await s.Queue.CompleteAsync(lease));
        Assert.DoesNotContain(s.Gateway.Messages, x => x.IdempotencyKey.StartsWith($"reminder:{id:N}:", StringComparison.Ordinal));
    }

    /// <summary>Rechecks start revision, participation, membership, preferences and account eligibility after staging an eligible reminder email.</summary>
    /// <param name="gate">Real invalidating operation or account eligibility control between staging and dispatch.</param>
    /// <returns>A task completing after original-delivery supersession without confusing collateral mandatory messages.</returns>
    [Theory]
    [InlineData("leave")]
    [InlineData("removal")]
    [InlineData("preferences")]
    [InlineData("start")]
    [InlineData("ineligible")]
    [InlineData("departed")]
    public async Task ReminderDispatcher_RechecksEligibilityAfterStaging_AndSuppressesOldDelivery(string gate)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var (owner, target, _, id, work) = await PrepareAsync(s);
        s.Clock.Now = work.DueUtc;
        Assert.True(await s.Runner.RunOnceAsync("scheduled"));
        NotificationDelivery staged;
        await using (var db = s.Read())
        {
            var notice = await db.Notifications.SingleAsync(x => x.SourceChangeId == work.Id);
            staged = await db.NotificationDeliveries.AsNoTracking().SingleAsync(x => x.NotificationId == notice.Id);
            Assert.Equal(WorkStatus.Pending, staged.Status);
        }
        await InvalidateAsync(s, owner, target, id, gate);
        await s.FlushAsync();
        await using var after = s.Read();
        var delivery = await after.NotificationDeliveries.SingleAsync(x => x.Id == staged.Id);
        Assert.Equal(WorkStatus.Superseded, delivery.Status);
        Assert.Null(delivery.ProviderMessageId);
        Assert.Null(delivery.LeaseId);
        Assert.Equal(staged.PayloadJson, delivery.PayloadJson);
        Assert.DoesNotContain(s.Gateway.Messages, x => x.IdempotencyKey == staged.DeduplicationKey);
        Assert.Single(await after.Notifications.Where(x => x.SourceChangeId == work.Id).ToArrayAsync());
    }

    /// <summary>Preserves the immutable scheduled instant despite retry-due changes, at the exact lateness edge, one tick later and the start cutoff.</summary>
    /// <param name="stage">Whether the delayed gate is handler execution or final dispatch after earlier staging.</param>
    /// <param name="boundary">Exact lateness edge, adjacent tick, or exact Quest start.</param>
    /// <returns>A task completing after source-specific outcomes and unchanged business payload assertions.</returns>
    [Theory]
    [InlineData("handler", "exact")]
    [InlineData("handler", "after")]
    [InlineData("handler", "start")]
    [InlineData("dispatch", "exact")]
    [InlineData("dispatch", "after")]
    [InlineData("dispatch", "start")]
    public async Task ReminderRetryDue_DoesNotExtendImmutableLatenessWindow(string stage, string boundary)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var (_, target, _, id, work) = await PrepareAsync(s);
        var payload = CoreCompositionScenario.Payload<ReminderPayload>(work.PayloadJson);
        s.Clock.Now = work.DueUtc;
        NotificationDelivery? originalDelivery = null;
        if (stage == "dispatch")
        {
            Assert.True(await s.Runner.RunOnceAsync("scheduled"));
            await using var staging = s.Read();
            var originalNotice = await staging.Notifications.SingleAsync(x => x.SourceChangeId == work.Id);
            originalDelivery = await staging.NotificationDeliveries.AsNoTracking().SingleAsync(x => x.NotificationId == originalNotice.Id);
            Assert.Equal(WorkStatus.Pending, originalDelivery.Status);
        }
        var instant = boundary == "start" ? payload.StartUtc :
            payload.ScheduledUtc.AddMinutes(2).AddTicks(boundary == "after" ? 1 : 0);
        if (originalDelivery is not null)
        {
            await using var retryContext = s.Read();
            var retry = await retryContext.NotificationDeliveries.SingleAsync(x => x.Id == originalDelivery.Id);
            retry.DueUtc = instant;
            await retryContext.SaveChangesAsync();
            await retryContext.Entry(retry).ReloadAsync();
            Assert.Equal(instant, retry.DueUtc);
            Assert.Equal(originalDelivery.PayloadJson, retry.PayloadJson);
        }
        else
        {
            await s.SetDueAsync(work.Id, instant);
            Assert.Equal(instant, (await s.WorkAsync(work.Id)).DueUtc);
        }
        Assert.Equal(work.PayloadJson, (await s.WorkAsync(work.Id)).PayloadJson);
        s.Clock.Now = instant;
        var index = s.Gateway.Messages.Count;
        if (stage == "handler")
            Assert.True(await s.Runner.RunOnceAsync("scheduled"));
        await s.DrainAsync("delivery");
        var eligible = boundary == "exact";
        var key = $"reminder:{id:N}:{target.Id:N}:{payload.StartRevision}:email";
        var sent = s.Gateway.Messages.Skip(index).ToArray();
        if (eligible)
        {
            var message = Assert.Single(sent);
            Assert.Equal(key, message.IdempotencyKey);
            EventCancellationCompositionTests.AssertEmail(message, target.Email, "Your joined Quest starts soon. Check Sidequest for current details.");
            Assert.Null(message.CalendarContent);
        }
        else
            Assert.Empty(sent);
        await using var db = s.Read();
        var notice = await db.Notifications.Where(x => x.SourceChangeId == work.Id).ToArrayAsync();
        Assert.Equal(eligible || stage == "dispatch" ? 1 : 0, notice.Length);
        var deliveries = await db.NotificationDeliveries.Where(x => x.DeduplicationKey == key).ToArrayAsync();
        if (eligible || stage == "dispatch")
        {
            var delivery = Assert.Single(deliveries);
            Assert.Equal(eligible ? WorkStatus.Completed : WorkStatus.Superseded, delivery.Status);
            Assert.Equal(eligible ? "synthetic-provider-receipt" : null, delivery.ProviderMessageId);
            Assert.Equal(instant, delivery.DueUtc);
            if (originalDelivery is not null)
                Assert.Equal(originalDelivery.PayloadJson, delivery.PayloadJson);
        }
        else
            Assert.Empty(deliveries);
        Assert.Equal(work.PayloadJson, (await s.WorkAsync(work.Id)).PayloadJson);
        Assert.Equal(WorkStatus.Completed, (await s.WorkAsync(work.Id)).Status);
    }

    private static async Task<(UserAccount Owner, UserAccount Target, UserAccount Follower, Guid QuestId, ScheduledWork Work)>
        PrepareAsync(CoreCompositionScenario s)
    {
        var owner = await s.AddUserAsync("owner");
        var target = await s.AddUserAsync("target");
        var follower = await s.AddUserAsync("follower");
        var id = await s.CreateQuestAsync(owner, QuestVisibility.Private);
        await s.Quests.InviteAsync(id, target.Id);
        await s.Quests.InviteAsync(id, follower.Id);
        await s.ParticipateAsync(id, follower, ParticipationCommand.Follow);
        s.ActAs(target);
        await s.Notifications.SavePreferencesAsync(new(false, false, true, 0.5m, null));
        await s.Quests.ParticipateAsync(id, ParticipationCommand.Join);
        await s.FlushAsync();
        var work = Assert.Single(await s.ScheduledAsync(WorkTypes.Reminder));
        Assert.Equal(target.Id, work.UserId);
        Assert.Equal(WorkStatus.Pending, work.Status);
        Assert.Equal(0, work.Attempts);
        return (owner, target, follower, id, work);
    }

    private static async Task InvalidateAsync(CoreCompositionScenario s, UserAccount owner, UserAccount target, Guid id, string gate)
    {
        switch (gate)
        {
            case "start":
                s.ActAs(owner);
                await s.Quests.EditAsync(id, await s.QuestVersionAsync(id), CoreCompositionScenario.QuestInput(QuestVisibility.Private, startHour: 14));
                break;
            case "leave":
                await s.ParticipateAsync(id, target, ParticipationCommand.Leave);
                break;
            case "removal":
                s.ActAs(s.Manager);
                await s.Events.RemoveMemberAsync(s.EventId, target.Id, "Membership ended");
                break;
            case "preferences":
                s.ActAs(target);
                await s.Notifications.SavePreferencesAsync(new(false, false, false, 0.5m, null));
                break;
            case "event":
                s.ActAs(s.Manager);
                await s.Events.ChangeStatusAsync(s.EventId, await s.EventVersionAsync(), EventStatus.Cancelled, "Event cancelled");
                break;
            case "quest":
                s.ActAs(owner);
                await s.Quests.ChangeStatusAsync(id, await s.QuestVersionAsync(id), QuestStatus.Cancelled, "Quest cancelled");
                break;
            case "departed":
            case "ineligible":
                await using (var db = s.Read())
                {
                    var user = await db.Users.SingleAsync(x => x.Id == target.Id);
                    if (gate == "departed")
                        user.DepartureVerifiedUtc = s.Clock.Now;
                    else
                        user.IsEligible = false;
                    await db.SaveChangesAsync();
                }
                break;
            case "late":
                break;
            default:
                throw new InvalidOperationException("Unknown eligibility gate.");
        }
    }

    private static void AssertReminder(ScheduledWork work, Guid quest, Guid user, long revision,
        DateTimeOffset start, DateTimeOffset due, decimal hours)
    {
        Assert.Equal(WorkTypes.Reminder, work.Type);
        Assert.Equal($"reminder:{quest:N}:{user:N}:{revision}", work.DeduplicationKey);
        Assert.Equal(quest, work.QuestId);
        Assert.Equal(user, work.UserId);
        Assert.Equal(WorkStatus.Pending, work.Status);
        Assert.Equal(due, work.DueUtc);
        var payload = CoreCompositionScenario.Payload<ReminderPayload>(work.PayloadJson);
        Assert.Equal(quest, payload.QuestId);
        Assert.Equal(user, payload.UserId);
        Assert.Equal(revision, payload.StartRevision);
        Assert.Equal(start, payload.StartUtc);
        Assert.Equal(due, payload.ScheduledUtc);
        Assert.Equal(hours, payload.ReminderHours);
    }
}
