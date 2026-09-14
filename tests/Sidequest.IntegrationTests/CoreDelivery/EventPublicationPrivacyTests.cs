using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Delivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Preserves never-published Event privacy independently of current lifecycle labels or configured membership.</summary>
public sealed class EventPublicationPrivacyTests
{
    /// <summary>Retained Event history hides Event, child and generic audience notifications from nonowners and denies Event preference mutations through archive.</summary>
    /// <param name="status">Current historical lifecycle label.</param>
    [Theory]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task CancelledUnpublishedEventHidesRecipientHistoryAndPreferenceAccess(EventStatus status)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await MarkNeverPublishedAsync(s, status);
        var notifications = new[]
        {
            new Notification { UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(), EventId = s.Seed.Event.Id,
                Kind = NotificationKind.EventCancelled, Summary = "Private Event history" },
            new Notification { UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(), EventId = s.Seed.Event.Id,
                QuestId = s.Seed.Quest.Id, Kind = NotificationKind.QuestUpdated, Summary = "Private child history" },
            new Notification { UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(), EventId = s.Seed.Event.Id,
                Kind = NotificationKind.AccessRemoved, IsAccessLossNotice = true, Summary = "Private access history" }
        };
        await FoundationSeed.PersistAsync(s.Database, notifications);
        var hidden = await s.Service.ListAsync(new());
        Assert.Empty(hidden.Items);
        Assert.Equal(0, hidden.TotalCount);
        Assert.Equal(0, await s.Service.UnreadCountAsync());
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(
            () => s.Service.MarkReadAsync(notifications[0].Id))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(
            () => s.Service.SetEventNewQuestEmailAsync(s.Seed.Event.Id, true))).Code);
        await using (var read = s.Database.CreateContext())
        {
            Assert.Empty(await read.EventNotificationPreferences.ToListAsync());
            Assert.False(await s.Policy.CanReadQuestAsync(read, await read.Quests.SingleAsync(), s.Seed.User.Id, CancellationToken.None));
            Assert.False(await s.Policy.CanReceiveAsync(read, new(Guid.NewGuid(), NotificationKind.AccessRemoved,
                s.Seed.Event.Id, null, null, [s.Seed.User.Id], s.Clock.Now, AffectedUserIds: [s.Seed.User.Id]),
                s.Seed.User.Id, s.Clock.Now, CancellationToken.None));
        }
        await FoundationSeed.PersistAsync(s.Database, new EventOwner { EventId = s.Seed.Event.Id, UserId = s.Seed.User.Id });
        Assert.Equal(3, (await s.Service.ListAsync(new())).TotalCount);
        Assert.Equal(3, await s.Service.UnreadCountAsync());
        await s.Service.SetEventNewQuestEmailAsync(s.Seed.Event.Id, true);
        await using var ownerRead = s.Database.CreateContext();
        Assert.True((await ownerRead.EventNotificationPreferences.SingleAsync()).NewQuestEmail);
        Assert.True(await s.Policy.CanReadQuestAsync(ownerRead, await ownerRead.Quests.SingleAsync(), s.Seed.User.Id, CancellationToken.None));
    }

    /// <summary>Late publication-history changes suppress queued audience mail and prevent fresh access-loss calendar intent for a never-published Event.</summary>
    /// <param name="status">Current historical lifecycle label after the retained cancellation.</param>
    [Theory]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task CancelledUnpublishedEventSuppressesPendingMailAndNewAudienceOutbox(EventStatus status)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.MembershipAdded, s.Seed.Event.Id,
            null, s.Seed.Other.Id, [s.Seed.User.Id], s.Clock.Now, AffectedUserIds: [s.Seed.User.Id]);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        await MarkNeverPublishedAsync(s, status);
        var gateway = new RecordingEmailGateway();
        var lease = (await s.Queue.ClaimAsync("delivery"))!;
        await new DeliveryDispatcher(s.Factory, s.Policy, s.Renderer, gateway, s.Clock, s.Options)
            .ExecuteAsync(lease, CancellationToken.None);
        await s.AddChangeAsync(NotificationKind.AccessRemoved, 8);
        await s.ProcessChangeAsync();
        Assert.Empty(gateway.Messages);
        Assert.Equal(0, await s.Service.UnreadCountAsync());
        await using var read = s.Database.CreateContext();
        Assert.Single(await read.Notifications.ToListAsync());
        Assert.Equal(WorkStatus.Superseded, (await read.NotificationDeliveries.SingleAsync()).Status);
        Assert.Empty(await read.CalendarDeliveryStates.ToListAsync());
        Assert.Equal(2, await read.OutboxMessages.CountAsync(x => x.Status == WorkStatus.Completed));
    }

    private static async Task MarkNeverPublishedAsync(DeliveryScenario s, EventStatus status)
    {
        await using var update = s.Database.CreateContext();
        (await update.Events.SingleAsync()).Status = status;
        update.EventStatusHistory.Add(new EventStatusHistory
        {
            EventId = s.Seed.Event.Id, Previous = EventStatus.Draft, Next = EventStatus.Cancelled,
            OccurredUtc = s.Clock.Now, Reason = "Unpublished Event cancelled."
        });
        await update.SaveChangesAsync();
    }
}
