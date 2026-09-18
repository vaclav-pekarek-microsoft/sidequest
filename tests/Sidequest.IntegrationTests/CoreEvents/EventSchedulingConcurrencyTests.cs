using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreBoundaries;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Reproduces simultaneous Event scheduling through real publication services in an exclusively owned cold SQL catalog.</summary>
public sealed class EventSchedulingConcurrencyTests
{
    /// <summary>Different Event publications reserve their absent completion keys before insert conversion and commit all publication effects exactly once.</summary>
    /// <returns>Completion after a SQL DMV-observed lock wait, two committed publications, exact durable content, and unchanged repeated-publication rejection.</returns>
    [Fact]
    public async Task ColdPublications_ReserveCompletionKeysAndCommitExactlyOnce()
    {
        var database = new SqlTestDatabase();
        try
        {
            await database.InitializeAsync();
            var first = await EventSchedulingScenario.CreateAsync(database);
            var second = await EventSchedulingScenario.CreateAsync(database);
            await using (var before = database.CreateContext())
                Assert.Empty(await before.ScheduledWork.ToListAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var firstGate = new ScheduledWorkReadGate();
            var secondGate = new ScheduledWorkReadGate();
            var firstService = first.Service(firstGate);
            var secondService = second.Service(secondGate);
            var firstVersion = (await firstService.GetAsync(first.EventId)).Summary.Version;
            var secondVersion = (await secondService.GetAsync(second.EventId)).Summary.Version;
            var firstPublish = firstService.ChangeStatusAsync(first.EventId, firstVersion, EventStatus.Active, "", deadline.Token);
            Task secondPublish = Task.CompletedTask;
            Task blocked = Task.CompletedTask;
            try
            {
                await firstGate.Read.Task.WaitAsync(deadline.Token);
                secondPublish = secondService.ChangeStatusAsync(second.EventId, secondVersion, EventStatus.Active, "", deadline.Token);
                var waiter = await secondGate.Started.Task.WaitAsync(deadline.Token);
                blocked = SqlBoundaryCoordinator.WaitForBlockAsync(database, waiter, await firstGate.Started.Task, observation.Token);
                // On the baseline both compatible shared reads finish. Release them together to
                // expose the actual insertion conflict, not merely a missing-lock timing assertion.
                var observed = await Task.WhenAny(blocked, secondGate.Read.Task).WaitAsync(deadline.Token);
                if (observed == blocked)
                    await blocked;
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                await Task.WhenAll(firstPublish, secondPublish);
                Assert.Same(blocked, observed);
            }
            finally
            {
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                deadline.Cancel();
                observation.Cancel();
                await Record.ExceptionAsync(() => Task.WhenAll(firstPublish, secondPublish, blocked));
            }
            foreach (var scenario in new[] { first, second })
            {
                var service = scenario.Service();
                var current = await service.GetAsync(scenario.EventId);
                var repeat = await Assert.ThrowsAsync<DomainException>(() =>
                    service.ChangeStatusAsync(scenario.EventId, current.Summary.Version, EventStatus.Active, ""));
                Assert.Equal(ErrorCode.Conflict, repeat.Code);
                Assert.Equal("This Event lifecycle transition is not allowed.", repeat.Message);
                await using var read = database.CreateContext();
                var item = await read.Events.SingleAsync(x => x.Id == scenario.EventId);
                Assert.Equal(EventStatus.Active, item.Status);
                Assert.Equal(new DateOnly(2026, 7, 15), item.StartDate);
                Assert.Equal(new DateOnly(2026, 7, 16), item.EndDate);
                Assert.Equal("Europe/Prague", item.TimeZoneId);
                var history = Assert.Single(await read.EventStatusHistory.Where(x => x.EventId == item.Id).ToListAsync());
                Assert.Equal(EventStatus.Draft, history.Previous);
                Assert.Equal(EventStatus.Active, history.Next);
                Assert.Equal(scenario.Owner.Id, history.ActorId);
                Assert.Equal(FoundationSeed.Now, history.OccurredUtc);
                Assert.Equal("Event published.", history.Reason);
                var audits = await read.AuditEntries.Where(x => x.ResourceId == item.Id).ToListAsync();
                Assert.Equal(3, audits.Count);
                Assert.Single(audits, x => x.Action == "Event.Created");
                Assert.Single(audits, x => x.Action == "Event.Active" && x.ActorId == scenario.Owner.Id);
                var audience = Assert.Single(audits, x => x.Action == "Audience.Published");
                var message = Assert.Single(await read.OutboxMessages.Where(x => x.AggregateId == item.Id).ToListAsync());
                var envelope = JsonSerializer.Deserialize<ChangeEnvelope>(message.PayloadJson)!;
                Assert.Equal(NotificationKind.MembershipAdded, envelope.Kind);
                Assert.Equal(scenario.Owner.Id, envelope.ActorId);
                Assert.Equal([scenario.Member.Id], envelope.RecipientIds);
                Assert.Equal(audience.CorrelationId, envelope.ChangeId.ToString("N"));
                var work = Assert.Single(await read.ScheduledWork.Where(x => x.DeduplicationKey == scenario.Key()).ToListAsync());
                Assert.Equal(WorkTypes.EventCompletion, work.Type);
                Assert.Equal(WorkStatus.Pending, work.Status);
                Assert.Equal(EventSchedulingScenario.End, work.DueUtc);
                Assert.Equal(new EventCompletionPayload(1, item.Id, EventSchedulingScenario.End),
                    JsonSerializer.Deserialize<EventCompletionPayload>(work.PayloadJson));
                Assert.Null(work.QuestId);
                Assert.Null(work.UserId);
            }
            await using var final = database.CreateContext();
            Assert.Equal(2, await final.ScheduledWork.CountAsync());
        }
        finally
        {
            await database.DisposeAsync();
        }
    }
}
