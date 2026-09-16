using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreBoundaries;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Exercises simultaneous private invitations through real services against an exclusively owned cold SQL catalog.</summary>
public sealed class QuestInvitationConcurrencyTests
{
    /// <summary>Invitations under different Event locks reserve missing keys before insertion and grant exactly one durable private-access intent each.</summary>
    /// <returns>Completion after an observed SQL lock wait, both committed grants, recipient access and repeat-safe audit/outbox assertions.</returns>
    [Fact]
    public async Task ColdInvitations_CommitExactlyOnceAndGrantPrivateAccess()
    {
        var database = new SqlTestDatabase();
        try
        {
            await database.InitializeAsync();
            var first = await QuestScenario.CreateAsync(database, true);
            var second = await QuestScenario.CreateAsync(database, true);
            await using (var before = database.CreateContext())
                Assert.Empty(await before.QuestInvitations.ToListAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var firstGate = new InvitationReadGate();
            var secondGate = new InvitationReadGate();
            var firstInvite = Service(first, firstGate).InviteAsync(first.Seed.Quest.Id, first.Seed.Other.Id, deadline.Token);
            Task secondInvite = Task.CompletedTask;
            Task blocked = Task.CompletedTask;
            try
            {
                await firstGate.Read.Task.WaitAsync(deadline.Token);
                secondInvite = Service(second, secondGate).InviteAsync(second.Seed.Quest.Id, second.Seed.Other.Id, deadline.Token);
                var waiter = await secondGate.Started.Task.WaitAsync(deadline.Token);
                blocked = SqlBoundaryCoordinator.WaitForBlockAsync(database, waiter, await firstGate.Started.Task, observation.Token);
                // Baseline shared reads both finish; releasing together exposes their actual insert conversion.
                var observed = await Task.WhenAny(blocked, secondGate.Read.Task).WaitAsync(deadline.Token);
                if (observed == blocked)
                    await blocked;
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                await Task.WhenAll(firstInvite, secondInvite);
                Assert.Same(blocked, observed);
            }
            finally
            {
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                deadline.Cancel();
                observation.Cancel();
                await Record.ExceptionAsync(() => Task.WhenAll(firstInvite, secondInvite, blocked));
            }
            foreach (var scenario in new[] { first, second })
            {
                var id = scenario.Seed.Quest.Id;
                var target = scenario.Seed.Other.Id;
                await scenario.Service().InviteAsync(id, target);
                var detail = await scenario.Service(scenario.Seed.Other).GetAsync(id);
                Assert.Equal(id, detail.Summary.Id);
                Assert.Equal(ParticipationStatus.None, detail.Summary.Participation);
                Assert.False(detail.Summary.IsOwner);
                await using var read = database.CreateContext();
                var invitation = Assert.Single(await read.QuestInvitations.Where(x => x.QuestId == id).ToListAsync());
                Assert.Equal(target, invitation.UserId);
                Assert.Equal(scenario.Seed.User.Id, invitation.InvitedById);
                Assert.Equal(scenario.Clock.GetUtcNow(), invitation.ChangedUtc);
                Assert.Equal(QuestInvitationStatus.Active, invitation.Status);
                var audit = Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == id).ToListAsync());
                Assert.Equal($"InvitationGranted:{target:N}", audit.Action);
                Assert.Equal(scenario.Seed.User.Id, audit.ActorId);
                var message = Assert.Single(await read.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync());
                var envelope = JsonSerializer.Deserialize<ChangeEnvelope>(message.PayloadJson)!;
                Assert.Equal(NotificationKind.QuestInvitation, envelope.Kind);
                Assert.Equal([target], envelope.RecipientIds);
                Assert.NotNull(envelope.AffectedUserIds);
                Assert.Equal([target], envelope.AffectedUserIds);
                Assert.Equal(audit.CorrelationId, envelope.ChangeId.ToString("N"));
                Assert.Empty(await read.Participations.Where(x => x.QuestId == id).ToListAsync());
                Assert.Equal(scenario.Seed.Quest.CalendarRevision,
                    (await read.Quests.SingleAsync(x => x.Id == id)).CalendarRevision);
            }
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static QuestService Service(QuestScenario scenario, InvitationReadGate gate) =>
        new(new ObservedContextFactory(scenario.Database, gate),
            new ResourceAccess(StubCurrentUser.For(scenario.Seed.User)), new ChangeWriter(), scenario.Clock, scenario.Reconciler);
}
