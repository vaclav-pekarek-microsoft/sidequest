using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreBoundaries;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Reproduces publication-history versus participation-insert cross-Event lock ordering through real application services.</summary>
public sealed class PublicationParticipationConcurrencyTests
{
    /// <summary>A valid publication waits for a nonowner's history read without blocking that actor's Join on unused participation audiences.</summary>
    /// <param name="publishFirst">Whether publication rather than Join reaches its pre-save boundary first.</param>
    /// <param name="privatePublication">Whether publication must omit public discovery fan-out.</param>
    /// <returns>Completion after a DMV-observed history wait, both commits and exact history, scheduling, audit, participation and delivery assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicationAndNonownerJoin_CommitWithoutCrossResourceCycle(bool publishFirst, bool privatePublication)
    {
        var database = new SqlTestDatabase();
        try
        {
            await database.InitializeAsync();
            var publishing = await QuestScenario.CreateAsync(database, privatePublication);
            var joining = await QuestScenario.CreateAsync(database);
            string version;
            await using (var setup = database.CreateContext())
            {
                var draft = await setup.Quests.SingleAsync(x => x.Id == publishing.Seed.Quest.Id);
                draft.Status = QuestStatus.Draft;
                await setup.SaveChangesAsync();
                version = Convert.ToBase64String(draft.Version);
                Assert.Empty(await setup.Participations.ToListAsync());
                Assert.Empty(await setup.QuestStatusHistory.ToListAsync());
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var publicationGate = new CrossResourceSaveGate();
            var joinGate = new CrossResourceSaveGate();
            Task PublishAsync() => ParticipationTestServices.Service(publishing, publishing.Seed.User, publicationGate)
                .ChangeStatusAsync(publishing.Seed.Quest.Id, version, QuestStatus.Active, "", deadline.Token);
            Task JoinAsync() => ParticipationTestServices.Service(joining, joining.Seed.Other, joinGate)
                .ParticipateAsync(joining.Seed.Quest.Id, ParticipationCommand.Join, deadline.Token);
            Task publication = Task.CompletedTask;
            Task join = Task.CompletedTask;
            try
            {
                if (publishFirst)
                {
                    publication = PublishAsync();
                    await publicationGate.Ready.Task.WaitAsync(deadline.Token);
                    join = JoinAsync();
                }
                else
                {
                    join = JoinAsync();
                    await joinGate.Ready.Task.WaitAsync(deadline.Token);
                    publication = PublishAsync();
                }
                await Task.WhenAll(publicationGate.Ready.Task, joinGate.Ready.Task).WaitAsync(deadline.Token);
                publicationGate.Release.TrySetResult();
                await SqlBoundaryCoordinator.WaitForBlockAsync(database,
                    await publicationGate.Session.Task, await joinGate.Session.Task, deadline.Token);
                joinGate.Release.TrySetResult();
                await Task.WhenAll(publication, join);
            }
            finally
            {
                publicationGate.Release.TrySetResult();
                joinGate.Release.TrySetResult();
                deadline.Cancel();
                await Record.ExceptionAsync(() => Task.WhenAll(publication, join));
            }
            await ParticipationTestServices.Service(joining).ParticipateAsync(joining.Seed.Quest.Id, ParticipationCommand.Join);
            await using var read = database.CreateContext();
            var quest = await read.Quests.SingleAsync(x => x.Id == publishing.Seed.Quest.Id);
            await ParticipationTestServices.Service(publishing, publishing.Seed.User)
                .ChangeStatusAsync(quest.Id, Convert.ToBase64String(quest.Version), QuestStatus.Active, "");
            Assert.Equal(QuestStatus.Active, quest.Status);
            Assert.Equal(publishing.Seed.Quest.CalendarRevision, quest.CalendarRevision);
            var history = Assert.Single(await read.QuestStatusHistory.ToListAsync());
            Assert.Equal(quest.Id, history.QuestId);
            Assert.Equal(QuestStatus.Draft, history.Previous);
            Assert.Equal(QuestStatus.Active, history.Next);
            Assert.Equal(publishing.Seed.User.Id, history.ActorId);
            var work = Assert.Single(await read.ScheduledWork.ToListAsync());
            Assert.StartsWith($"quest-complete:{quest.Id:N}:{quest.EndUtc.UtcTicks}:", work.DeduplicationKey);
            Assert.Equal(quest.EndUtc, work.DueUtc);
            Assert.Equal(WorkTypes.QuestCompletion, work.Type);
            var publicationAudit = await read.AuditEntries.SingleAsync(x => x.ResourceId == quest.Id);
            Assert.Equal("Status:Draft->Active", publicationAudit.Action);
            var publicationMessages = await read.OutboxMessages.Where(x => x.AggregateId == quest.Id).ToListAsync();
            if (privatePublication)
                Assert.Empty(publicationMessages);
            else
            {
                var envelope = JsonSerializer.Deserialize<ChangeEnvelope>(Assert.Single(publicationMessages).PayloadJson)!;
                Assert.Equal(NotificationKind.QuestPublished, envelope.Kind);
                Assert.Empty(envelope.RecipientIds);
                Assert.NotNull(envelope.PreviousAttendeeIds);
                Assert.Empty(envelope.PreviousAttendeeIds);
                Assert.Equal(publicationAudit.CorrelationId, envelope.ChangeId.ToString("N"));
            }
            var participant = Assert.Single(await read.Participations.ToListAsync());
            Assert.Equal(joining.Seed.Quest.Id, participant.QuestId);
            Assert.Equal(joining.Seed.Other.Id, participant.UserId);
            Assert.Equal(ParticipationStatus.Joined, participant.Status);
            var joinAudit = await read.AuditEntries.SingleAsync(x => x.ResourceId == participant.QuestId);
            Assert.Equal($"Participation:{participant.UserId:N}:None->Joined", joinAudit.Action);
            var joined = JsonSerializer.Deserialize<ChangeEnvelope>(
                (await read.OutboxMessages.SingleAsync(x => x.AggregateId == participant.QuestId)).PayloadJson)!;
            Assert.Equal(NotificationKind.Joined, joined.Kind);
            Assert.Equal(new[] { joining.Seed.User.Id, participant.UserId }.Order(), joined.RecipientIds.Order());
            Assert.Equal(joining.Seed.Quest.CalendarRevision + 1, joined.CalendarRevision);
            Assert.Equal(joinAudit.CorrelationId, joined.ChangeId.ToString("N"));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }
}
