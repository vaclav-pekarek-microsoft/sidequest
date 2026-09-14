using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Delivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Verifies transport fanout for coalesced Event cancellation versus direct full-audience Quest cancellation.</summary>
public sealed class EventCancellationDeliveryTests
{
    /// <summary>Parent cancellation compensates an uncertain request only before the child end; an already-staged cancellation still submits after end.</summary>
    /// <param name="ticksFromEnd">Clock offset immediately before, at or after the child's exclusive end.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UncertainParentCancellationRespectsChildEndBoundary(long ticksFromEnd)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway { Failure = new DeliveryTransportException(TransportOutcome.Uncertain, "timeout") };
        var dispatcher = new DeliveryDispatcher(s.Factory, s.Policy, s.Renderer, gateway, s.Clock, s.Options);
        var first = (await s.Queue.ClaimAsync("delivery"))!;
        await Assert.ThrowsAsync<DeliveryTransportException>(() => dispatcher.ExecuteAsync(first, CancellationToken.None));
        await s.Queue.FailAsync(first, gateway.Failure);
        await using (var update = s.Database.CreateContext())
        {
            var quest = await update.Quests.SingleAsync();
            s.Clock.Now = quest.EndUtc.AddTicks(ticksFromEnd);
            quest.Status = ticksFromEnd < 0 ? QuestStatus.Active : QuestStatus.Completed;
            (await update.Events.SingleAsync()).Status = EventStatus.Cancelled;
            await update.SaveChangesAsync();
        }
        var retry = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(retry, CancellationToken.None);
        Assert.Single(gateway.Messages);
        await using (var read = s.Database.CreateContext())
        {
            Assert.Equal(ticksFromEnd < 0 ? 2 : 1, await read.NotificationDeliveries.CountAsync());
            Assert.Equal(ticksFromEnd < 0 ? "CANCEL" : "REQUEST", (await read.CalendarDeliveryStates.SingleAsync()).IntendedMethod);
            Assert.Equal(ticksFromEnd < 0 ? 8 : 7, (await read.Quests.SingleAsync()).CalendarRevision);
        }
        if (ticksFromEnd < 0)
        {
            s.Clock.Now += TimeSpan.FromSeconds(1);
            gateway.Failure = null;
            var withdrawal = (await s.Queue.ClaimAsync("delivery"))!;
            await dispatcher.ExecuteAsync(withdrawal, CancellationToken.None);
            Assert.True(await s.Queue.CompleteAsync(withdrawal));
            Assert.Equal(2, gateway.Messages.Count);
            Assert.Equal("CANCEL", gateway.Messages.Last().CalendarMethod);
        }
        else
            Assert.Null(await s.Queue.ClaimAsync("delivery"));
    }

    /// <summary>Event-caused attendee-only child envelopes retain every calendar withdrawal while nonattendees get one parent status; direct cancellations retain each child status.</summary>
    /// <param name="eventCancellation">Whether cancellation comes from the parent Event rather than direct Quest commands.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationFanoutPreservesCalendarsAndCoalescesOnlyParentStatus(bool eventCancellation)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var invitee = FoundationSeed.NewUser();
        invitee.Email = "invitee@example.invalid";
        var secondQuest = FoundationSeed.NewQuest(s.Seed.Event.Id, s.Seed.Other.Id);
        secondQuest.StartUtc = s.Clock.Now.AddHours(1);
        secondQuest.EndUtc = s.Clock.Now.AddHours(2);
        secondQuest.StartRevision = 1;
        await FoundationSeed.PersistAsync(s.Database, invitee, secondQuest);
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.User.Id)).LastSignedInUtc = s.Clock.Now;
            (await update.Users.SingleAsync(x => x.Id == s.Seed.Other.Id)).Email = "owner-follower@example.invalid";
            update.EventMemberships.AddRange(s.Seed.Membership(s.Seed.Other.Id), s.Seed.Membership(invitee.Id));
            update.Participations.Add(new QuestParticipation
            {
                QuestId = secondQuest.Id, UserId = s.Seed.User.Id, Status = ParticipationStatus.Joined, ChangedUtc = s.Clock.Now
            });
            foreach (var quest in await update.Quests.ToListAsync())
            {
                quest.Status = QuestStatus.Cancelled;
                quest.CalendarRevision = 8;
                update.QuestOwners.Add(new QuestOwner { QuestId = quest.Id, UserId = s.Seed.Other.Id });
                update.Participations.Add(new QuestParticipation
                {
                    QuestId = quest.Id, UserId = s.Seed.Other.Id, Status = ParticipationStatus.Following, ChangedUtc = s.Clock.Now
                });
                update.QuestInvitations.Add(new QuestInvitation
                {
                    QuestId = quest.Id, UserId = invitee.Id, Status = QuestInvitationStatus.Active,
                    InvitedById = s.Seed.Other.Id, ChangedUtc = s.Clock.Now
                });
            }
            if (eventCancellation)
                (await update.Events.SingleAsync()).Status = EventStatus.Cancelled;
            await update.SaveChangesAsync();
        }
        Guid[] audience = [s.Seed.User.Id, s.Seed.Other.Id, invitee.Id];
        if (eventCancellation)
        {
            await s.AddChangeAsync(new ChangeEnvelope(Guid.NewGuid(), NotificationKind.EventCancelled, s.Seed.Event.Id,
                null, s.Seed.Other.Id, audience, s.Clock.Now));
            await s.ProcessChangeAsync();
        }
        foreach (var questId in new[] { s.Seed.Quest.Id, secondQuest.Id })
        {
            await s.AddChangeAsync(new ChangeEnvelope(Guid.NewGuid(),
                eventCancellation ? NotificationKind.EventCancelled : NotificationKind.QuestCancelled,
                s.Seed.Event.Id, questId, s.Seed.Other.Id, eventCancellation ? [s.Seed.User.Id] : audience,
                s.Clock.Now, 8, PreviousAttendeeIds: [s.Seed.User.Id], CalendarChanged: true));
            await s.ProcessChangeAsync();
        }
        var gateway = new RecordingEmailGateway();
        var dispatcher = new DeliveryDispatcher(s.Factory, s.Policy, s.Renderer, gateway, s.Clock, s.Options);
        var expected = eventCancellation ? 5 : 6;
        for (var index = 0; index < expected; index++)
        {
            var lease = await s.Queue.ClaimAsync("delivery");
            Assert.NotNull(lease);
            await dispatcher.ExecuteAsync(lease, CancellationToken.None);
            Assert.True(await s.Queue.CompleteAsync(lease));
        }
        Assert.Null(await s.Queue.ClaimAsync("delivery"));
        Assert.Equal(expected, gateway.Messages.Count);
        Assert.Equal(eventCancellation ? 1 : 2, gateway.Messages.Count(x => x.Recipient == "owner-follower@example.invalid"));
        Assert.Equal(eventCancellation ? 1 : 2, gateway.Messages.Count(x => x.Recipient == invitee.Email));
        var calendars = gateway.Messages.Where(x => x.CalendarContent is not null).ToArray();
        Assert.Equal(2, calendars.Length);
        foreach (var message in calendars)
        {
            Assert.Equal(s.Seed.User.Email, message.Recipient);
            Assert.Equal("CANCEL", message.CalendarMethod);
            Assert.DoesNotContain("owner-follower@example.invalid", message.CalendarContent!);
            Assert.DoesNotContain(invitee.Email, message.CalendarContent!);
            Assert.DoesNotContain(s.Seed.Quest.Title, message.CalendarContent!);
        }
        Assert.Contains(calendars, x => x.CalendarContent!.Contains($"{s.Seed.Quest.Id:N}@sidequest.calendar", StringComparison.Ordinal));
        Assert.Contains(calendars, x => x.CalendarContent!.Contains($"{secondQuest.Id:N}@sidequest.calendar", StringComparison.Ordinal));
        await using var read = s.Database.CreateContext();
        Assert.Null((await read.Users.SingleAsync(x => x.Id == s.Seed.Other.Id)).LastSignedInUtc);
        Assert.Null((await read.Users.SingleAsync(x => x.Id == invitee.Id)).LastSignedInUtc);
        var deliveries = await read.NotificationDeliveries.ToListAsync();
        Assert.Equal(eventCancellation ? 3 : 0, deliveries.Count(x =>
            JsonSerializer.Deserialize<DeliveryPayload>(x.PayloadJson)!.Change.QuestId is null));
        Assert.All(deliveries, x => Assert.Equal(WorkStatus.Completed, x.Status));
        Assert.Equal(2, await read.CalendarDeliveryStates.CountAsync(x => x.IntendedMethod == "CANCEL" && x.SentSequence == 8));
        Assert.Empty(await read.ScheduledWork.ToListAsync());
    }
}
