using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Checks exact all-status completion deduplication, rescheduling, authorization, and atomic Event publication on SQL.</summary>
/// <param name="database">The uniquely owned migrated SQL fixture for this class.</param>
public sealed class EventSchedulingTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Publication and active-date edits preserve an existing exact intent in every state, including its independently shifted retry due time.</summary>
    /// <param name="edit">Whether the producer edits an Active Event to a new end instead of publishing a Draft.</param>
    /// <param name="status">The retained intent state that must continue to suppress an insertion.</param>
    /// <returns>Completion after successful producer execution, unchanged work identity/content/version, and no duplicate on a repeated edit.</returns>
    [Theory]
    [InlineData(false, WorkStatus.Pending)]
    [InlineData(false, WorkStatus.Processing)]
    [InlineData(false, WorkStatus.Completed)]
    [InlineData(false, WorkStatus.DeadLetter)]
    [InlineData(false, WorkStatus.Superseded)]
    [InlineData(true, WorkStatus.Pending)]
    [InlineData(true, WorkStatus.Processing)]
    [InlineData(true, WorkStatus.Completed)]
    [InlineData(true, WorkStatus.DeadLetter)]
    [InlineData(true, WorkStatus.Superseded)]
    public async Task ExactIntent_AllStatusesRemainDeduplicated(bool edit, WorkStatus status)
    {
        var scenario = await EventSchedulingScenario.CreateAsync(database);
        if (edit)
            await ProduceAsync(scenario, false);
        var end = EventSchedulingScenario.End.AddDays(edit ? 1 : 0);
        var existing = scenario.Work(status, end: end);
        existing.DueUtc = end.AddHours(2);
        existing.Attempts = 3;
        existing.LastError = "Retained scheduling history.";
        await FoundationSeed.PersistAsync(database, existing);
        await ProduceAsync(scenario, edit);
        var service = scenario.Service();
        var input = Input(edit);
        await service.EditAsync(scenario.EventId, (await service.GetAsync(scenario.EventId)).Summary.Version, input);
        await using var read = database.CreateContext();
        var item = await read.Events.SingleAsync(x => x.Id == scenario.EventId);
        Assert.Equal(EventStatus.Active, item.Status);
        Assert.Equal(input.StartDate, item.StartDate);
        Assert.Equal(input.EndDate, item.EndDate);
        var work = await read.ScheduledWork.SingleAsync(x => x.DeduplicationKey == scenario.Key(end));
        Assert.Equal(existing.Id, work.Id);
        Assert.Equal(existing.Version, work.Version);
        Assert.Equal(status, work.Status);
        Assert.Equal(existing.DueUtc, work.DueUtc);
        Assert.Equal(existing.PayloadJson, work.PayloadJson);
        Assert.Equal(3, work.Attempts);
        Assert.Equal(existing.LastError, work.LastError);
        Assert.Equal(edit ? 2 : 1, await read.ScheduledWork.CountAsync(x => x.DeduplicationKey.StartsWith($"event.complete.v1:{scenario.EventId:N}:")));
        Assert.Single(await read.EventStatusHistory.Where(x => x.EventId == scenario.EventId).ToListAsync());
        Assert.Single(await read.OutboxMessages.Where(x => x.AggregateId == scenario.EventId).ToListAsync());
    }

    /// <summary>A pending key that only extends the desired key cannot suppress publication or rescheduling of the exact Event deadline.</summary>
    /// <param name="edit">Whether the producer is an Active Event date edit rather than publication.</param>
    /// <returns>Completion after exact-key insertion alongside the untouched prefix lookalike.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefixLookalike_DoesNotSuppressExactIntent(bool edit)
    {
        var scenario = await EventSchedulingScenario.CreateAsync(database);
        if (edit)
            await ProduceAsync(scenario, false);
        var end = EventSchedulingScenario.End.AddDays(edit ? 1 : 0);
        var lookalike = scenario.Work(WorkStatus.Pending, scenario.Key(end) + ":attempt", end);
        await FoundationSeed.PersistAsync(database, lookalike);
        await ProduceAsync(scenario, edit);
        await using var read = database.CreateContext();
        var inserted = await read.ScheduledWork.SingleAsync(x => x.DeduplicationKey == scenario.Key(end));
        Assert.NotEqual(lookalike.Id, inserted.Id);
        Assert.Equal(WorkStatus.Pending, inserted.Status);
        Assert.Equal(end, inserted.DueUtc);
        Assert.Equal(lookalike.Version, (await read.ScheduledWork.SingleAsync(x => x.Id == lookalike.Id)).Version);
        Assert.Equal(edit ? 3 : 2, await read.ScheduledWork.CountAsync(x => x.DeduplicationKey.StartsWith($"event.complete.v1:{scenario.EventId:N}:")));
    }

    /// <summary>End-date changes create one immutable intent per deadline, start-only edits reuse it, and stale work cannot complete an extended Event.</summary>
    /// <returns>Completion after extension/reversion/re-extension, unchanged keys, early/stale execution checks, and exactly-once completion at the new boundary.</returns>
    [Fact]
    public async Task Rescheduling_ReusesExactDeadlinesAndPreservesCompletionBoundary()
    {
        var scenario = await EventSchedulingScenario.CreateAsync(database);
        await ProduceAsync(scenario, false);
        await ProduceAsync(scenario, true);
        var service = scenario.Service();
        foreach (var input in new[]
        {
            Input(false), Input(true), Input(true) with { StartDate = new DateOnly(2026, 7, 14) }
        })
            await service.EditAsync(scenario.EventId, (await service.GetAsync(scenario.EventId)).Summary.Version, input);
        ScheduledWork original;
        ScheduledWork extended;
        await using (var read = database.CreateContext())
        {
            var rows = await read.ScheduledWork.Where(x => x.DeduplicationKey.StartsWith($"event.complete.v1:{scenario.EventId:N}:")).ToListAsync();
            Assert.Equal(2, rows.Count);
            original = Assert.Single(rows, x => x.DeduplicationKey == scenario.Key());
            extended = Assert.Single(rows, x => x.DeduplicationKey == scenario.Key(EventSchedulingScenario.End.AddDays(1)));
            Assert.Equal(EventSchedulingScenario.End, original.DueUtc);
            Assert.Equal(EventSchedulingScenario.End.AddDays(1), extended.DueUtc);
            var item = await read.Events.SingleAsync(x => x.Id == scenario.EventId);
            Assert.Equal(new DateOnly(2026, 7, 14), item.StartDate);
            Assert.Equal(new DateOnly(2026, 7, 17), item.EndDate);
        }
        scenario.Context.Clock.Now = EventSchedulingScenario.End;
        await scenario.Context.Completion().ExecuteAsync(original.Id, default);
        scenario.Context.Clock.Now = extended.DueUtc.AddTicks(-1);
        var early = await Assert.ThrowsAsync<DomainException>(() => scenario.Context.Completion().ExecuteAsync(extended.Id, default));
        Assert.Equal(ErrorCode.Conflict, early.Code);
        Assert.Equal("Event completion is not due yet.", early.Message);
        await using (var stillActive = database.CreateContext())
            Assert.Equal(EventStatus.Active, (await stillActive.Events.SingleAsync(x => x.Id == scenario.EventId)).Status);
        scenario.Context.Clock.Now = extended.DueUtc;
        await scenario.Context.Completion().ExecuteAsync(extended.Id, default);
        await scenario.Context.Completion().ExecuteAsync(extended.Id, default);
        await using var final = database.CreateContext();
        Assert.Equal(EventStatus.Completed, (await final.Events.SingleAsync(x => x.Id == scenario.EventId)).Status);
        var completion = Assert.Single(await final.EventStatusHistory.Where(x => x.EventId == scenario.EventId && x.Next == EventStatus.Completed).ToListAsync());
        Assert.Equal(extended.DueUtc, completion.OccurredUtc);
        Assert.Single(await final.AuditEntries.Where(x => x.ResourceId == scenario.EventId && x.Action == "Event.Completed").ToListAsync());
        Assert.Equal(original.Version, (await final.ScheduledWork.SingleAsync(x => x.Id == original.Id)).Version);
        Assert.Equal(extended.Version, (await final.ScheduledWork.SingleAsync(x => x.Id == extended.Id)).Version);
        Assert.Single(await final.OutboxMessages.Where(x => x.AggregateId == scenario.EventId).ToListAsync());
    }

    /// <summary>Membership without ownership cannot reach the scheduling reservation during publication or an Active Event edit.</summary>
    /// <param name="edit">Whether an already published Event is being edited.</param>
    /// <returns>Completion after safe authorization denial, no scheduling query, and unchanged aggregate rowversion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nonowner_CannotReachScheduling(bool edit)
    {
        var scenario = await EventSchedulingScenario.CreateAsync(database);
        if (edit)
            await ProduceAsync(scenario, false);
        var version = (await scenario.Service().GetAsync(scenario.EventId)).Summary.Version;
        var gate = new ScheduledWorkReadGate();
        gate.Release.TrySetResult();
        var service = scenario.Service(gate, scenario.Member);
        var error = await Assert.ThrowsAsync<DomainException>(() => edit
            ? service.EditAsync(scenario.EventId, version, Input(true))
            : service.ChangeStatusAsync(scenario.EventId, version, EventStatus.Active, ""));
        Assert.Equal(ErrorCode.NotFound, error.Code);
        Assert.False(gate.Started.Task.IsCompleted);
        Assert.Equal(version, (await scenario.Service().GetAsync(scenario.EventId)).Summary.Version);
    }

    /// <summary>Failure or cancellation after SQL saves publication rolls back the Event, history, audits, outbox and completion intent together.</summary>
    /// <param name="cancel">Whether the injected post-save failure is cooperative cancellation.</param>
    /// <returns>Completion after the original failure propagates and fresh reads prove no partial publication or scheduling.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostSaveFailure_RollsBackPublicationAndSchedule(bool cancel)
    {
        var scenario = await EventSchedulingScenario.CreateAsync(database);
        Exception failure = cancel ? new OperationCanceledException("Controlled publication cancellation.") :
            new InvalidOperationException("Controlled publication failure.");
        var observer = new FailAfterPublicationSave(failure);
        var version = (await scenario.Service().GetAsync(scenario.EventId)).Summary.Version;
        Assert.Same(failure, await Record.ExceptionAsync(() => scenario.Service(observer)
            .ChangeStatusAsync(scenario.EventId, version, EventStatus.Active, "")));
        Assert.True(observer.Saved);
        await using (var read = database.CreateContext())
        {
            var item = await read.Events.SingleAsync(x => x.Id == scenario.EventId);
            Assert.Equal(EventStatus.Draft, item.Status);
            Assert.Equal(version, Convert.ToBase64String(item.Version));
            Assert.Empty(await read.EventStatusHistory.Where(x => x.EventId == item.Id).ToListAsync());
            Assert.Equal("Event.Created", (await read.AuditEntries.SingleAsync(x => x.ResourceId == item.Id)).Action);
            Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == item.Id).ToListAsync());
            Assert.False(await read.ScheduledWork.AnyAsync(x => x.DeduplicationKey == scenario.Key()));
        }
        await ProduceAsync(scenario, false);
        await using var final = database.CreateContext();
        Assert.Equal(EventStatus.Active, (await final.Events.SingleAsync(x => x.Id == scenario.EventId)).Status);
        Assert.Single(await final.ScheduledWork.Where(x => x.DeduplicationKey == scenario.Key()).ToListAsync());
    }

    private static EventInput Input(bool extended) =>
        EventTestContext.Input() with { EndDate = new DateOnly(2026, 7, extended ? 17 : 16) };

    private static async Task ProduceAsync(EventSchedulingScenario scenario, bool edit)
    {
        var service = scenario.Service();
        var version = (await service.GetAsync(scenario.EventId)).Summary.Version;
        if (edit)
            await service.EditAsync(scenario.EventId, version, Input(true));
        else
            await service.ChangeStatusAsync(scenario.EventId, version, EventStatus.Active, "");
    }

    private sealed class FailAfterPublicationSave(Exception failure) : SaveChangesInterceptor
    {
        internal bool Saved { get; private set; }

        /// <inheritdoc />
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            Assert.Equal(6, result);
            Assert.Equal(EventStatus.Active, Assert.Single(db.ChangeTracker.Entries<Event>()).Entity.Status);
            Assert.Equal(EntityState.Unchanged, Assert.Single(db.ChangeTracker.Entries<EventStatusHistory>()).State);
            Assert.Equal(2, db.ChangeTracker.Entries<AuditEntry>().Count());
            Assert.Equal(EntityState.Unchanged, Assert.Single(db.ChangeTracker.Entries<OutboxMessage>()).State);
            Assert.Equal(EntityState.Unchanged, Assert.Single(db.ChangeTracker.Entries<ScheduledWork>()).State);
            Saved = true;
            throw failure;
        }
    }
}
