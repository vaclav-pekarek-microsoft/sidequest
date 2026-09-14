using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.CoreComposition;

/// <summary>Exercises atomic ownership denial, real handoff and immutable access-loss delivery across Event and Quest services.</summary>
public sealed class MembershipCompositionTests
{
    /// <summary>Denies membership removal while an ownership assignment remains without changing any relevant persisted fact.</summary>
    /// <param name="eventOwner">Whether the blocking assignment belongs to the Event rather than its private child.</param>
    /// <returns>A task completing after byte-for-byte persisted snapshot and no-email assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveEventMember_WhileEventOrQuestOwner_RejectsAtomically(bool eventOwner)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var target = await s.AddUserAsync("target");
        var id = await s.CreateQuestAsync(owner, QuestVisibility.Private);
        await s.Quests.InviteAsync(id, target.Id);
        if (!eventOwner)
            await s.Quests.AddOwnerAsync(id, target.Id);
        else
        {
            await using var db = s.Read();
            db.EventOwners.Add(new EventOwner { EventId = s.EventId, UserId = target.Id });
            await db.SaveChangesAsync();
        }
        await s.ParticipateAsync(id, target, ParticipationCommand.Join);
        await s.FlushAsync();
        var before = await SnapshotAsync(s);
        var emails = s.Gateway.Messages.ToArray();
        s.ActAs(s.Manager);
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Events.RemoveMemberAsync(s.EventId, target.Id, "Remove access"));
        Assert.Equal(ErrorCode.Conflict, error.Code);
        Assert.Equal("Remove all Event and child Quest ownership assignments before removing membership.", error.Message);
        Assert.Equal(before, await SnapshotAsync(s));
        Assert.Equal(emails, s.Gateway.Messages);
    }

    /// <summary>Hands off ownership, removes a private member, changes a same-instant observer and delivers only immutable target notices.</summary>
    /// <param name="joined">Whether the target has actual attendance requiring a calendar withdrawal rather than only following.</param>
    /// <param name="remindersEnabled">Whether removal itself must supersede an enabled pending reminder.</param>
    /// <returns>A task completing after cascade, continued owner access, exact gateway and repeat-command assertions.</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RemoveEventMember_AfterOwnerHandoff_ImmutableTargetSurvivesObserverChangesAndMandatoryDelivery(
        bool joined, bool remindersEnabled)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("retained-owner");
        var target = await s.AddUserAsync("removed-target");
        var observer = await s.AddUserAsync("observer");
        var id = await s.CreateQuestAsync(owner, QuestVisibility.Private);
        await s.Quests.InviteAsync(id, target.Id);
        await s.Quests.InviteAsync(id, observer.Id);
        await s.Quests.AddOwnerAsync(id, target.Id);
        await s.ParticipateAsync(id, target, joined ? ParticipationCommand.Join : ParticipationCommand.Follow);
        await s.ParticipateAsync(id, observer, ParticipationCommand.Follow);
        await s.FlushAsync();
        var originalQuest = await s.QuestAsync(id);
        var oldRequest = joined ? Assert.Single(s.Gateway.Messages, x => x.Recipient == target.Email && x.CalendarMethod == "REQUEST") : null;
        s.ActAs(target);
        await s.Notifications.SavePreferencesAsync(new(false, false, remindersEnabled, 0.5m, null));
        s.ActAs(owner);
        await s.Quests.RemoveOwnerAsync(id, target.Id);
        await s.FlushAsync();
        await using (var db = s.Read())
        {
            db.EventInvitations.Add(new EventInvitation
            {
                EventId = s.EventId, UserId = target.Id, InvitedById = s.Manager.Id,
                Status = EventInvitationStatus.Pending, CreatedUtc = s.Clock.Now,
                ExpiresUtc = s.Clock.Now.AddHours(2)
            });
            await db.SaveChangesAsync();
        }
        var pendingReminders = (await s.ScheduledAsync(WorkTypes.Reminder)).Where(x => x.UserId == target.Id).ToArray();
        if (joined && remindersEnabled)
            Assert.Equal(WorkStatus.Pending, Assert.Single(pendingReminders).Status);
        var baseline = (await s.OutboxAsync()).Select(x => x.Id).ToHashSet();
        var messageIndex = s.Gateway.Messages.Count;
        s.ActAs(s.Manager);
        await s.Events.RemoveMemberAsync(s.EventId, target.Id, "Assignment ended");
        var originalRows = (await s.OutboxAsync()).Where(x => !baseline.Contains(x.Id)).ToArray();
        Assert.Equal(2, originalRows.Length);
        await EventCancellationCompositionTests.AssertChangeIdentityAsync(s, originalRows);
        var changes = originalRows.Select(x => CoreCompositionScenario.Payload<ChangeEnvelope>(x.PayloadJson)).ToArray();
        var child = Assert.Single(changes, x => x.QuestId == id);
        var parent = Assert.Single(changes, x => x.QuestId is null);
        Assert.All(changes, x =>
        {
            Assert.Equal(NotificationKind.AccessRemoved, x.Kind);
            Assert.Equal(s.Manager.Id, x.ActorId);
            Assert.Equal(new[] { target.Id }, x.AffectedUserIds);
            Assert.Equal(s.Clock.Now, x.OccurredUtc);
            Assert.Equal("Assignment ended", x.Reason);
        });
        Assert.Equal(new[] { target.Id }, parent.RecipientIds);
        Assert.Equal((joined ? new[] { owner.Id, target.Id } : [target.Id]).Order(), child.RecipientIds.Order());
        Assert.Equal(joined ? new[] { target.Id } : [], child.PreviousAttendeeIds);
        Assert.Equal(joined, child.CalendarChanged);
        Assert.True(child.MaterialChange);
        Assert.Equal(originalQuest.CalendarRevision + (joined ? 1 : 0), child.CalendarRevision);
        // The observer changes at the identical timestamp, then a third actor processes the original work.
        await s.ParticipateAsync(id, observer, ParticipationCommand.Unfollow);
        s.ActAs(owner);
        var retained = await s.Quests.GetAsync(id);
        Assert.True(retained.Summary.IsOwner);
        await s.Quests.EditAsync(id, await s.QuestVersionAsync(id),
            CoreCompositionScenario.QuestInput(QuestVisibility.Private) with { SuggestedCapacity = 15 });
        s.ActAs(target);
        var denied = await Assert.ThrowsAsync<DomainException>(() => s.Quests.GetAsync(id));
        Assert.Equal(ErrorCode.NotFound, denied.Code);
        s.ActAs(observer);
        await s.FlushAsync();
        var sources = originalRows.Select(x => x.Id).ToHashSet();
        var keys = originalRows.Select(x => $"change:{x.Id:N}:{target.Id:N}:email").Order().ToArray();
        var messages = s.Gateway.Messages.Skip(messageIndex).Where(x => keys.Contains(x.IdempotencyKey)).ToArray();
        Assert.Equal(keys, messages.Select(x => x.IdempotencyKey).Order());
        Assert.All(messages, x => EventCancellationCompositionTests.AssertEmail(x, target.Email,
            "Your access has changed. Previously shared content may no longer be available."));
        Assert.Null(Assert.Single(messages, x => x.IdempotencyKey == $"change:{parent.ChangeId:N}:{target.Id:N}:email").CalendarContent);
        if (joined)
        {
            var withdrawal = Assert.Single(messages, x => x.CalendarMethod == "CANCEL");
            EventCancellationCompositionTests.AssertCalendar(withdrawal, id, originalQuest.CalendarRevision + 1,
                "CANCEL", target.Email, s.Clock.Now, originalQuest.StartUtc, originalQuest.EndUtc);
            Assert.Contains($"UID:{id:N}@sidequest.calendar", oldRequest!.CalendarContent!);
            Assert.DoesNotContain("sentinel", withdrawal.CalendarContent!);
        }
        else
            Assert.All(messages, x => Assert.Null(x.CalendarContent));
        await using (var db = s.Read())
        {
            var membership = await db.EventMemberships.SingleAsync(x => x.UserId == target.Id);
            Assert.Equal(MembershipStatus.Removed, membership.Status);
            Assert.Equal(s.Manager.Id, membership.ChangedById);
            Assert.Equal(s.Clock.Now, membership.ChangedUtc);
            var privateGrant = await db.QuestInvitations.SingleAsync(x => x.UserId == target.Id);
            Assert.Equal(QuestInvitationStatus.Revoked, privateGrant.Status);
            Assert.Equal(s.Clock.Now, privateGrant.ChangedUtc);
            var targetParticipation = await db.Participations.SingleAsync(x => x.UserId == target.Id);
            var observerParticipation = await db.Participations.SingleAsync(x => x.UserId == observer.Id);
            Assert.Equal(ParticipationStatus.None, targetParticipation.Status);
            Assert.Equal(ParticipationStatus.None, observerParticipation.Status);
            Assert.Equal(s.Clock.Now, targetParticipation.ChangedUtc);
            Assert.Equal(targetParticipation.ChangedUtc, observerParticipation.ChangedUtc);
            var invitation = await db.EventInvitations.SingleAsync();
            Assert.Equal(EventInvitationStatus.Revoked, invitation.Status);
            Assert.Equal(s.Clock.Now, invitation.ResolvedUtc);
            var notices = await db.Notifications.Where(x => sources.Contains(x.SourceChangeId)).ToArrayAsync();
            Assert.Equal(new[] { target.Id }, notices.Where(x => x.SourceChangeId == parent.ChangeId).Select(x => x.UserId));
            Assert.Equal((joined ? new[] { target.Id, owner.Id } : [target.Id]).Order(),
                notices.Where(x => x.SourceChangeId == child.ChangeId).Select(x => x.UserId).Order());
            var noticeIds = notices.Select(x => x.Id).ToArray();
            var deliveries = await db.NotificationDeliveries.Where(x => noticeIds.Contains(x.NotificationId)).ToArrayAsync();
            Assert.Equal(keys, deliveries.Select(x => x.DeduplicationKey).Order());
            Assert.All(deliveries, x =>
            {
                Assert.Equal(target.Id, x.UserId);
                Assert.Equal(WorkStatus.Completed, x.Status);
                Assert.Equal("synthetic-provider-receipt", x.ProviderMessageId);
            });
            foreach (var original in originalRows)
                Assert.Equal(original.PayloadJson, (await db.OutboxMessages.SingleAsync(x => x.Id == original.Id)).PayloadJson);
        }
        foreach (var reminder in pendingReminders)
        {
            var after = await s.WorkAsync(reminder.Id);
            Assert.Equal(WorkStatus.Superseded, after.Status);
            Assert.Null(after.LeaseId);
        }
        var beforeRepeat = await SnapshotAsync(s);
        s.ActAs(s.Manager);
        await s.Events.RemoveMemberAsync(s.EventId, target.Id, "Assignment ended");
        Assert.Equal(beforeRepeat, await SnapshotAsync(s));
        s.Clock.Now = originalQuest.StartUtc.AddMinutes(-10);
        Assert.False(await s.Runner.RunOnceAsync("scheduled"));
        Assert.DoesNotContain(s.Gateway.Messages, x => x.IdempotencyKey.StartsWith($"reminder:{id:N}:{target.Id:N}:", StringComparison.Ordinal));
        Assert.Equal(0, s.Directory.UserCalls);
    }

    /// <summary>Removes an Event owner assignment through the real service before the same manager removes membership.</summary>
    /// <returns>A task completing after exact parent access notice delivery and retained-manager access.</returns>
    [Fact]
    public async Task RemoveEventOwner_ThenRemoveMember_RetainsManagerAndDeliversAccessNotice()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var target = await s.AddUserAsync("target");
        await using (var db = s.Read())
        {
            db.EventOwners.Add(new EventOwner { EventId = s.EventId, UserId = target.Id });
            await db.SaveChangesAsync();
        }
        await s.Events.RemoveOwnerAsync(s.EventId, target.Id);
        await s.FlushAsync();
        var index = s.Gateway.Messages.Count;
        await s.Events.RemoveMemberAsync(s.EventId, target.Id, "Handoff complete");
        await s.FlushAsync();
        EventCancellationCompositionTests.AssertEmail(Assert.Single(s.Gateway.Messages.Skip(index)), target.Email,
            "Your access has changed. Previously shared content may no longer be available.");
        Assert.True((await s.Events.GetAsync(s.EventId)).Summary.IsOwner);
        await using var after = s.Read();
        Assert.Equal(new[] { s.Manager.Id }, await after.EventOwners.Select(x => x.UserId).ToArrayAsync());
        Assert.Equal(MembershipStatus.Removed, (await after.EventMemberships.SingleAsync(x => x.UserId == target.Id)).Status);
    }

    private static async Task<string> SnapshotAsync(CoreCompositionScenario s)
    {
        await using var db = s.Read();
        return JsonSerializer.Serialize(new
        {
            Events = await db.Events.OrderBy(x => x.Id).ToArrayAsync(),
            Members = await db.EventMemberships.OrderBy(x => x.Id).ToArrayAsync(),
            EventOwners = await db.EventOwners.OrderBy(x => x.Id).ToArrayAsync(),
            Owners = await db.QuestOwners.OrderBy(x => x.Id).ToArrayAsync(),
            Invitations = await db.QuestInvitations.OrderBy(x => x.Id).ToArrayAsync(),
            EventInvitations = await db.EventInvitations.OrderBy(x => x.Id).ToArrayAsync(),
            Participation = await db.Participations.OrderBy(x => x.Id).ToArrayAsync(),
            Quests = await db.Quests.OrderBy(x => x.Id).ToArrayAsync(),
            History = await db.QuestStatusHistory.OrderBy(x => x.Id).ToArrayAsync(),
            EventHistory = await db.EventStatusHistory.OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.AuditEntries.OrderBy(x => x.Id).ToArrayAsync(),
            Outbox = await db.OutboxMessages.OrderBy(x => x.Id).ToArrayAsync(),
            Calendar = await db.CalendarDeliveryStates.OrderBy(x => x.Id).ToArrayAsync(),
            Scheduled = await db.ScheduledWork.OrderBy(x => x.Id).ToArrayAsync()
        });
    }
}
