using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreBoundaries;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Exercises complete participation read/write paths under different Event locks against cold and retained SQL rows.</summary>
public sealed class ParticipationConcurrencyTests
{
    /// <summary>Independent private-Quest mutations reserve participation before save without sharing unrelated PK audience ranges.</summary>
    /// <param name="initial">The retained actor state, or null for a completely cold participation table.</param>
    /// <param name="command">A valid exclusive participation transition for both actors.</param>
    /// <returns>Completion after SQL blocking or independent progress, exact durable state/delivery effects, repeat safety, and advisory-capacity assertions.</returns>
    [Theory]
    [InlineData(null, ParticipationCommand.Join)]
    [InlineData(null, ParticipationCommand.Follow)]
    [InlineData(ParticipationStatus.None, ParticipationCommand.Join)]
    [InlineData(ParticipationStatus.Following, ParticipationCommand.Join)]
    [InlineData(ParticipationStatus.Joined, ParticipationCommand.Leave)]
    [InlineData(ParticipationStatus.Following, ParticipationCommand.Unfollow)]
    public async Task DifferentEvents_CommitExclusiveParticipationExactlyOnce(ParticipationStatus? initial, ParticipationCommand command)
    {
        var database = new SqlTestDatabase();
        try
        {
            await database.InitializeAsync();
            var scenarios = new[] { await QuestScenario.CreateAsync(database, true), await QuestScenario.CreateAsync(database, true) };
            var retained = new Dictionary<Guid, QuestParticipation>();
            foreach (var scenario in scenarios)
            {
                await scenario.Service().InviteAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id);
                await using var setup = database.CreateContext();
                (await setup.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).SuggestedCapacity = 1;
                if (initial is not null)
                {
                    var actor = new QuestParticipation
                    {
                        QuestId = scenario.Seed.Quest.Id,
                        UserId = scenario.Seed.Other.Id,
                        Status = initial.Value,
                        ChangedUtc = scenario.Clock.GetUtcNow().AddDays(-1)
                    };
                    setup.Participations.AddRange(actor, new QuestParticipation
                    {
                        QuestId = actor.QuestId,
                        UserId = scenario.Seed.User.Id,
                        Status = ParticipationStatus.Joined,
                        ChangedUtc = actor.ChangedUtc
                    });
                    retained.Add(actor.QuestId, actor);
                }
                await setup.SaveChangesAsync();
            }
            await using (var before = database.CreateContext())
                Assert.Equal(initial is null ? 0 : 4, await before.Participations.CountAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var firstGate = new ParticipationMutationGate();
            var secondGate = new ParticipationMutationGate();
            var first = Service(scenarios[0], firstGate).ParticipateAsync(scenarios[0].Seed.Quest.Id, command, deadline.Token);
            Task second = Task.CompletedTask;
            Task blocked = Task.CompletedTask;
            try
            {
                await firstGate.Ready.Task.WaitAsync(deadline.Token);
                second = Service(scenarios[1], secondGate).ParticipateAsync(scenarios[1].Seed.Quest.Id, command, deadline.Token);
                var waiter = await secondGate.Session.Task.WaitAsync(deadline.Token);
                blocked = SqlBoundaryCoordinator.WaitForBlockAsync(database, waiter, await firstGate.Session.Task, observation.Token);
                // Pause after every audience read, not merely the actor lookup: a key-only
                // reservation can still leave shared PK scans that deadlock at insertion/update.
                var observed = await Task.WhenAny(blocked, secondGate.Ready.Task).WaitAsync(deadline.Token);
                if (observed == blocked)
                    await blocked;
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                await Task.WhenAll(first, second);
                if (initial is null)
                    Assert.Same(blocked, observed);
                else
                    Assert.Same(secondGate.Ready.Task, observed);
            }
            finally
            {
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                deadline.Cancel();
                observation.Cancel();
                await Record.ExceptionAsync(() => Task.WhenAll(first, second, blocked));
            }
            var previous = initial ?? ParticipationStatus.None;
            var next = command switch
            {
                ParticipationCommand.Join => ParticipationStatus.Joined,
                ParticipationCommand.Follow => ParticipationStatus.Following,
                _ => ParticipationStatus.None
            };
            var calendar = previous == ParticipationStatus.Joined || next == ParticipationStatus.Joined;
            foreach (var scenario in scenarios)
            {
                var id = scenario.Seed.Quest.Id;
                var actorId = scenario.Seed.Other.Id;
                await using var read = database.CreateContext();
                var firstSaved = await read.Participations.AsNoTracking().SingleAsync(x => x.QuestId == id && x.UserId == actorId);
                await ParticipationTestServices.Service(scenario).ParticipateAsync(id, command);
                var participant = await read.Participations.SingleAsync(x => x.QuestId == id && x.UserId == actorId);
                Assert.Equal(firstSaved.Id, participant.Id);
                Assert.Equal(firstSaved.Version, participant.Version);
                Assert.Equal(next, participant.Status);
                Assert.Equal(scenario.Clock.GetUtcNow(), participant.ChangedUtc);
                if (initial is not null)
                {
                    Assert.Equal(retained[id].Id, participant.Id);
                    Assert.NotEqual(retained[id].Version, participant.Version);
                    Assert.Equal(ParticipationStatus.Joined,
                        (await read.Participations.SingleAsync(x => x.QuestId == id && x.UserId == scenario.Seed.User.Id)).Status);
                }
                Assert.Equal(initial is null ? 1 : 2, await read.Participations.CountAsync(x => x.QuestId == id));
                var quest = await read.Quests.SingleAsync(x => x.Id == id);
                Assert.Equal(scenario.Seed.Quest.CalendarRevision + (calendar ? 1 : 0), quest.CalendarRevision);
                Assert.Equal(scenario.Clock.GetUtcNow(), quest.UpdatedUtc);
                Assert.Equal(QuestStatus.Active, quest.Status);
                Assert.Equal(1, quest.SuggestedCapacity);
                Assert.Equal((initial is null ? 0 : 1) + (next == ParticipationStatus.Joined ? 1 : 0),
                    await read.Participations.CountAsync(x => x.QuestId == id && x.Status == ParticipationStatus.Joined));
                var audit = Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == id && x.Action.StartsWith("Participation:")).ToListAsync());
                Assert.Equal($"Participation:{actorId:N}:{previous}->{next}", audit.Action);
                Assert.Equal(actorId, audit.ActorId);
                Assert.Equal(ResourceKind.Quest, audit.ResourceKind);
                Assert.Equal("", audit.Reason);
                Assert.Equal(scenario.Clock.GetUtcNow(), audit.OccurredUtc);
                Assert.Equal(2, await read.AuditEntries.CountAsync(x => x.ResourceId == id));
                Assert.Empty(await read.QuestStatusHistory.Where(x => x.QuestId == id).ToListAsync());
                var messages = await read.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync();
                Assert.Equal(calendar ? 2 : 1, messages.Count);
                var changes = messages.Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!)
                    .Where(x => x.Kind != NotificationKind.QuestInvitation).ToArray();
                if (calendar)
                {
                    var envelope = Assert.Single(changes);
                    Assert.Equal(next == ParticipationStatus.Joined ? NotificationKind.Joined : NotificationKind.Left, envelope.Kind);
                    Assert.Equal(new[] { scenario.Seed.User.Id, actorId }.Order(), envelope.RecipientIds.Order());
                    Assert.NotNull(envelope.AffectedUserIds);
                    Assert.Equal([actorId], envelope.AffectedUserIds);
                    Assert.NotNull(envelope.PreviousAttendeeIds);
                    Assert.Equal(previous == ParticipationStatus.Joined ? new[] { actorId } : [], envelope.PreviousAttendeeIds);
                    Assert.Equal(actorId, envelope.ActorId);
                    Assert.Equal(scenario.Seed.Event.Id, envelope.EventId);
                    Assert.Equal(id, envelope.QuestId);
                    Assert.Equal(quest.CalendarRevision, envelope.CalendarRevision);
                    Assert.True(envelope.CalendarChanged);
                    Assert.False(envelope.MaterialChange);
                    Assert.Equal(scenario.Clock.GetUtcNow(), envelope.OccurredUtc);
                    Assert.Equal(audit.CorrelationId, envelope.ChangeId.ToString("N"));
                    var message = Assert.Single(messages, x => x.Id == envelope.ChangeId);
                    Assert.Equal(WorkTypes.Change, message.Type);
                    Assert.Equal(WorkStatus.Pending, message.Status);
                    Assert.Equal(audit.CorrelationId, message.CorrelationId);
                    Assert.Equal(envelope.OccurredUtc, message.DueUtc);
                }
                else
                    Assert.Empty(changes);
                Assert.Equal(QuestInvitationStatus.Active, (await read.QuestInvitations.SingleAsync(x => x.QuestId == id)).Status);
            }
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static QuestService Service(QuestScenario scenario, ParticipationMutationGate gate) =>
        ParticipationTestServices.Service(scenario, scenario.Seed.Other, gate, gate.Commands);
}
