using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Recipient and administrator service contracts tested through real operation-scoped SQL contexts.</summary>
public sealed class NotificationServiceTests
{
    /// <summary>Inbox paging and unread count hide protected items immediately after membership loss while generic notices have no links.</summary>
    [Fact]
    public async Task ReadTimeMembershipLossHidesProtectedItemsAndRedactsGenericLinks()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var protectedItem = new Notification { UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(),
            Kind = NotificationKind.QuestUpdated, EventId = s.Seed.Event.Id, QuestId = s.Seed.Quest.Id,
            Summary = "private sentinel", CreatedUtc = s.Clock.Now };
        var generic = new Notification { UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(),
            Kind = NotificationKind.AccessRemoved, EventId = s.Seed.Event.Id, QuestId = s.Seed.Quest.Id,
            Summary = NotificationRules.Summary(NotificationKind.AccessRemoved), IsAccessLossNotice = true, CreatedUtc = s.Clock.Now };
        var otherItem = new Notification { UserId = s.Seed.Other.Id, SourceChangeId = Guid.NewGuid(), Summary = "another recipient", CreatedUtc = s.Clock.Now };
        await FoundationSeed.PersistAsync(s.Database, protectedItem, generic, otherItem);
        Assert.Equal(2, (await s.Service.ListAsync(new())).TotalCount);
        Assert.Equal(2, await s.Service.UnreadCountAsync());
        await using (var update = s.Database.CreateContext())
        {
            (await update.EventMemberships.SingleAsync()).Status = MembershipStatus.Removed;
            await update.SaveChangesAsync();
        }
        var page = await s.Service.ListAsync(new());
        var item = Assert.Single(page.Items);
        Assert.Equal(generic.Id, item.Id);
        Assert.Null(item.QuestId);
        Assert.Null(item.EventId);
        Assert.DoesNotContain("private", item.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, await s.Service.UnreadCountAsync());
        var hidden = await Assert.ThrowsAsync<DomainException>(() => s.Service.MarkReadAsync(protectedItem.Id));
        Assert.Equal(ErrorCode.NotFound, hidden.Code);
        var others = await Assert.ThrowsAsync<DomainException>(() => s.Service.MarkReadAsync(otherItem.Id));
        Assert.Equal(ErrorCode.NotFound, others.Code);
        await s.Service.MarkReadAsync(null);
        Assert.Equal(0, await s.Service.UnreadCountAsync());
        await using var read = s.Database.CreateContext();
        Assert.Null((await read.Notifications.SingleAsync(x => x.Id == otherItem.Id)).ReadUtc);
    }

    /// <summary>Private invitation revocation and verified departure deny subsequent reads even on an existing service instance.</summary>
    [Fact]
    public async Task PrivateInvitationRevocationAndDepartureReauthorizeEveryRead()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await using (var update = s.Database.CreateContext())
        {
            (await update.Quests.SingleAsync()).Visibility = QuestVisibility.Private;
            update.QuestInvitations.Add(s.Seed.Invitation());
            update.Notifications.Add(new Notification { UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(),
                EventId = s.Seed.Event.Id, QuestId = s.Seed.Quest.Id, Summary = "private" });
            await update.SaveChangesAsync();
        }
        Assert.Equal(1, await s.Service.UnreadCountAsync());
        await using (var update = s.Database.CreateContext())
        {
            (await update.QuestInvitations.SingleAsync()).Status = QuestInvitationStatus.Revoked;
            await update.SaveChangesAsync();
        }
        Assert.Equal(0, await s.Service.UnreadCountAsync());
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.User.Id)).DepartureVerifiedUtc = s.Clock.Now;
            await update.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => s.Service.GetPreferencesAsync())).Code);
    }

    /// <summary>Defaults and exact decimal preference changes persist alongside reminder replacement and an audit record.</summary>
    [Fact]
    public async Task PreferencesPersistExactlyAndReplaceReminderWithoutNewLogicalKey()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var defaults = await s.Service.GetPreferencesAsync();
        Assert.False(defaults.NewQuestEmail);
        Assert.True(defaults.ActivityEmail);
        Assert.True(defaults.RemindersEnabled);
        Assert.Equal(1m, defaults.ReminderHours);
        await s.Service.SavePreferencesAsync(new(true, false, true, .5m, "Europe/Prague"));
        var first = await s.Service.GetPreferencesAsync();
        Assert.Equal(.5m, first.ReminderHours);
        Assert.Equal("Europe/Prague", first.TimeZoneId);
        Guid id;
        await using (var read = s.Database.CreateContext())
        {
            var work = await read.ScheduledWork.SingleAsync();
            id = work.Id;
            Assert.Equal(s.Clock.Now.AddMinutes(30), work.DueUtc);
            Assert.Equal(1, await read.AuditEntries.CountAsync());
        }
        await s.Service.SavePreferencesAsync(first with { ReminderHours = 2 });
        await using (var read = s.Database.CreateContext())
        {
            var work = await read.ScheduledWork.SingleAsync();
            Assert.Equal(id, work.Id);
            Assert.Equal(s.Clock.Now, work.DueUtc);
        }
        await s.Service.SavePreferencesAsync(first with { RemindersEnabled = false });
        await using var final = s.Database.CreateContext();
        Assert.Equal(WorkStatus.Superseded, (await final.ScheduledWork.SingleAsync()).Status);
        Assert.Equal(3, await final.AuditEntries.CountAsync());
        Assert.Empty(await final.NotificationDeliveries.ToListAsync());
    }

    /// <summary>Invalid precision causes no database mutation; per-Event override requires current individual membership.</summary>
    [Fact]
    public async Task InvalidPreferencesAreAtomicAndEventOverrideRequiresMembership()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await Assert.ThrowsAsync<DomainException>(() => s.Service.SavePreferencesAsync(new(false, true, true, .001m, null)));
        await using (var read = s.Database.CreateContext())
        {
            Assert.Empty(await read.NotificationPreferences.ToListAsync());
            Assert.Empty(await read.ScheduledWork.ToListAsync());
            Assert.Empty(await read.AuditEntries.ToListAsync());
        }
        await s.Service.SetEventNewQuestEmailAsync(s.Seed.Event.Id, true);
        await using (var update = s.Database.CreateContext())
        {
            Assert.True((await update.EventNotificationPreferences.SingleAsync()).NewQuestEmail);
            (await update.EventMemberships.SingleAsync()).Status = MembershipStatus.Removed;
            await update.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.NotFound,
            (await Assert.ThrowsAsync<DomainException>(() => s.Service.SetEventNewQuestEmailAsync(s.Seed.Event.Id, false))).Code);
    }

    /// <summary>Calendar recovery is joined-only, active-only and recipient-only; suspending the Quest denies a fresh download.</summary>
    [Fact]
    public async Task DownloadCalendarRequiresCurrentJoinedActiveAccess()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var content = await s.Service.DownloadCalendarAsync(s.Seed.Quest.Id);
        Assert.Contains($"{s.Seed.Quest.Id:N}@sidequest.calendar", content);
        Assert.Contains("mailto:ada@example.invalid", content);
        Assert.DoesNotContain("identity@example.invalid", content);
        await using (var update = s.Database.CreateContext())
        {
            (await update.Participations.SingleAsync()).Status = ParticipationStatus.Following;
            await update.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => s.Service.DownloadCalendarAsync(s.Seed.Quest.Id))).Code);
        await using (var update = s.Database.CreateContext())
        {
            (await update.Participations.SingleAsync()).Status = ParticipationStatus.Joined;
            (await update.Quests.SingleAsync()).Status = QuestStatus.Suspended;
            await update.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<DomainException>(() => s.Service.DownloadCalendarAsync(s.Seed.Quest.Id));
    }

    /// <summary>Failure diagnostics redact persisted secrets; only a current administrator may replay a dead letter with the original logical key.</summary>
    [Fact]
    public async Task FailureReplayIsAdministratorOnlyRedactedAndPreservesLogicalIdentity()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var work = new ScheduledWork { Type = "unknown.v1", DeduplicationKey = "original-key", PayloadJson = "private-secret",
            LastError = "address@example.invalid credential secret", Status = WorkStatus.DeadLetter, Attempts = 8, DueUtc = s.Clock.Now };
        await FoundationSeed.PersistAsync(s.Database, work);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => s.Service.FailedDeliveriesAsync())).Code);
        await FoundationSeed.PersistAsync(s.Database, new Administrator { UserId = s.Seed.User.Id });
        var failure = Assert.Single(await s.Service.FailedDeliveriesAsync());
        Assert.Equal(work.Id, failure.Id);
        Assert.Equal("scheduled", failure.Kind);
        Assert.DoesNotContain("secret", failure.Error);
        Assert.DoesNotContain("@", failure.Error);
        await Assert.ThrowsAsync<DomainException>(() => s.Service.ReplayAsync(work.Id, "arbitrary"));
        await s.Service.ReplayAsync(work.Id, failure.Kind);
        var repeated = await Assert.ThrowsAsync<DomainException>(() => s.Service.ReplayAsync(work.Id, failure.Kind));
        Assert.Equal(ErrorCode.Conflict, repeated.Code);
        await using var read = s.Database.CreateContext();
        var saved = await read.ScheduledWork.SingleAsync();
        Assert.Equal(WorkStatus.Pending, saved.Status);
        Assert.Equal(0, saved.Attempts);
        Assert.Equal("original-key", saved.DeduplicationKey);
        Assert.Equal("private-secret", saved.PayloadJson);
        Assert.Equal("notification.work.replayed", (await read.AuditEntries.SingleAsync()).Action);
    }

    /// <summary>Cancelled unpublished content retains draft-level privacy, including after archive; membership alone cannot disclose a retained notification.</summary>
    /// <param name="status">Historical non-active lifecycle state after unpublished cancellation.</param>
    [Theory]
    [InlineData(QuestStatus.Cancelled)]
    [InlineData(QuestStatus.Archived)]
    public async Task CancelledUnpublishedQuestRetainsOwnerOnlyNotificationPrivacy(QuestStatus status)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await using (var update = s.Database.CreateContext())
        {
            (await update.Quests.SingleAsync()).Status = status;
            update.QuestStatusHistory.Add(new QuestStatusHistory
            {
                QuestId = s.Seed.Quest.Id, Previous = QuestStatus.Draft, Next = QuestStatus.Cancelled,
                OccurredUtc = s.Clock.Now, Reason = "Unpublished draft cancelled."
            });
            update.Notifications.Add(new Notification
            {
                UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(), Kind = NotificationKind.QuestCancelled,
                EventId = s.Seed.Event.Id, QuestId = s.Seed.Quest.Id, Summary = "Unpublished private content"
            });
            update.Notifications.Add(new Notification
            {
                UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(), Kind = NotificationKind.SuspendedQuestEdited,
                EventId = s.Seed.Event.Id, QuestId = s.Seed.Quest.Id, Summary = "Moderator-only content", IsAccessLossNotice = true
            });
            await update.SaveChangesAsync();
        }
        Assert.Equal(0, await s.Service.UnreadCountAsync());
        Assert.Empty((await s.Service.ListAsync(new())).Items);
        await using (var read = s.Database.CreateContext())
            Assert.False(await s.Policy.CanReadQuestAsync(read, await read.Quests.SingleAsync(), s.Seed.User.Id, CancellationToken.None));
        await FoundationSeed.PersistAsync(s.Database, new QuestOwner { QuestId = s.Seed.Quest.Id, UserId = s.Seed.User.Id });
        Assert.Equal(1, await s.Service.UnreadCountAsync());
        await using var ownerRead = s.Database.CreateContext();
        Assert.True(await s.Policy.CanReadQuestAsync(ownerRead, await ownerRead.Quests.SingleAsync(), s.Seed.User.Id, CancellationToken.None));
        Assert.False(await s.Policy.CanReceiveAsync(ownerRead, new(Guid.NewGuid(), NotificationKind.SuspendedQuestEdited,
            s.Seed.Event.Id, s.Seed.Quest.Id, null, [s.Seed.User.Id], s.Clock.Now), s.Seed.User.Id, s.Clock.Now, CancellationToken.None));
    }
}
