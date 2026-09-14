using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Background;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Validates durable Event completion payloads and repeat-safe atomic execution against migrated SQL.</summary>
/// <param name="database">Unique migrated database owned by the existing foundation fixture.</param>
public sealed class EventCompletionTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Rejects unknown schema, wrong type, malformed JSON, empty identity and absent immutable boundaries without child work.</summary>
    /// <param name="partition">Independent invalid payload partition.</param>
    /// <returns>A task completing after validation and no-change assertions.</returns>
    [Theory]
    [InlineData("schema")]
    [InlineData("type")]
    [InlineData("json")]
    [InlineData("null")]
    [InlineData("identity")]
    [InlineData("boundary")]
    public async Task InvalidCompletionPayloadCannotChangeAggregate(string partition)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var end = TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End;
        var work = Work(seed.Event.Id, end);
        if (partition == "schema")
            work.PayloadJson = JsonSerializer.Serialize(new EventCompletionPayload(2, seed.Event.Id, end));
        if (partition == "type")
            work.Type = WorkTypes.BulkMembership;
        if (partition == "json")
            work.PayloadJson = "{";
        if (partition == "null")
            work.PayloadJson = "null";
        if (partition == "identity")
            work.PayloadJson = JsonSerializer.Serialize(new EventCompletionPayload(1, Guid.Empty, end));
        if (partition == "boundary")
            work.PayloadJson = JsonSerializer.Serialize(new EventCompletionPayload(1, seed.Event.Id, default));
        await FoundationSeed.PersistAsync(database, work);
        context.Clock.Now = end.AddDays(1);
        var error = await Assert.ThrowsAsync<DomainException>(() => context.Completion().ExecuteAsync(work.Id, default));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Empty(context.Quests.Calls);
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Active, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Empty(await read.EventStatusHistory.Where(x => x.EventId == seed.Event.Id).ToListAsync());
    }

    /// <summary>Rejects work one tick early; at the deadline completes children, closes pending consent and records exactly one transition across retries.</summary>
    /// <returns>A task completing after before/at boundary execution and replay persistence checks.</returns>
    [Fact]
    public async Task DueCompletionClosesPendingWorkAndReexecutionHasNoEffects()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var end = TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End;
        var work = Work(seed.Event.Id, end);
        await FoundationSeed.PersistAsync(database, work,
            new EventMembershipRequest { EventId = seed.Event.Id, UserId = seed.Other.Id, CreatedUtc = FoundationSeed.Now },
            new EventInvitation { EventId = seed.Event.Id, UserId = seed.Other.Id, InvitedById = seed.User.Id, CreatedUtc = FoundationSeed.Now, ExpiresUtc = end });
        context.Clock.Now = end.AddTicks(-1);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => context.Completion().ExecuteAsync(work.Id, default))).Code);
        Assert.Empty(context.Quests.Calls);
        context.Clock.Now = end;
        await context.Completion().ExecuteAsync(work.Id, default);
        await context.Completion().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Completed, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Completed, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
        var request = await read.MembershipRequests.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(MembershipRequestStatus.Rejected, request.Status);
        Assert.Equal(end, request.DecidedUtc);
        Assert.Null(request.DecidedById);
        Assert.Contains("inclusive local end date", request.Reason);
        var invitation = await read.EventInvitations.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(EventInvitationStatus.Expired, invitation.Status);
        Assert.Equal(end, invitation.ResolvedUtc);
        Assert.Single(await read.EventStatusHistory.Where(x => x.EventId == seed.Event.Id).ToListAsync());
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync());
        Assert.Equal(("Complete", seed.Event.Id, (Guid?)null, (Guid?)null, "", end), Assert.Single(context.Quests.Calls));
        Assert.Equal(work.Status, (await read.ScheduledWork.SingleAsync(x => x.Id == work.Id)).Status);
    }

    /// <summary>Ignores an obsolete saved boundary after an Event date change without completing it at the old deadline.</summary>
    /// <returns>A task completing after no-op handler execution and persisted state checks.</returns>
    [Fact]
    public async Task StaleCompletionBoundaryDoesNotCompleteExtendedEvent()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var previousEnd = TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End;
        var work = Work(seed.Event.Id, previousEnd);
        await FoundationSeed.PersistAsync(database, work);
        await using (var setup = database.CreateContext())
        {
            (await setup.Events.FindAsync(seed.Event.Id))!.EndDate = seed.Event.EndDate.AddDays(1);
            await setup.SaveChangesAsync();
        }
        context.Clock.Now = previousEnd;
        await context.Completion().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Active, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Active, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
        Assert.Empty(await read.EventStatusHistory.Where(x => x.EventId == seed.Event.Id).ToListAsync());
        Assert.Empty(context.Quests.Calls);
    }

    /// <summary>Uses the real queue backoff and administrator replay paths without treating the shifted execution time as payload corruption.</summary>
    /// <param name="replay">Whether an administrator replays a dead letter instead of the queue retrying a transient failure.</param>
    /// <returns>A task completing after exactly-once parent/child effects and dispatcher-owned lease/state assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriedOrReplayedCompletionUsesImmutableBoundary(bool replay)
    {
        await using var scenario = await DeliveryScenario.CreateAsync();
        var end = TimeRules.EventWindow(scenario.Seed.Event.StartDate, scenario.Seed.Event.EndDate, scenario.Seed.Event.TimeZoneId).End;
        var work = Work(scenario.Seed.Event.Id, end);
        await FoundationSeed.PersistAsync(scenario.Database, work);
        scenario.Clock.Now = end;
        var first = Assert.IsType<WorkLease>(await scenario.Queue.ClaimAsync("scheduled"));
        Assert.Equal(work.Id, first.Id);
        Assert.True(await scenario.Queue.FailAsync(first, new DomainException(
            replay ? ErrorCode.Validation : ErrorCode.DependencyUnavailable, "Controlled initial failure.")));
        await using (var failed = scenario.Database.CreateContext())
        {
            var saved = await failed.ScheduledWork.SingleAsync();
            Assert.Equal(replay ? WorkStatus.DeadLetter : WorkStatus.Pending, saved.Status);
            Assert.True(saved.DueUtc > end);
            Assert.Equal(work.PayloadJson, saved.PayloadJson);
            scenario.Clock.Now = saved.DueUtc;
        }
        if (replay)
        {
            await FoundationSeed.PersistAsync(scenario.Database, new Administrator { UserId = scenario.Seed.User.Id });
            scenario.Clock.Now = end.AddMinutes(5);
            await scenario.Service.ReplayAsync(work.Id, "scheduled");
        }
        var lease = Assert.IsType<WorkLease>(await scenario.Queue.ClaimAsync("scheduled"));
        Assert.Equal(work.Id, lease.Id);
        var quests = new RecordingQuestLifecycle();
        var handler = new EventCompletionHandler(scenario.Factory, quests, scenario.Clock);
        await handler.ExecuteAsync(work.Id, default);
        await handler.ExecuteAsync(work.Id, default);
        await using (var read = scenario.Database.CreateContext())
        {
            Assert.Equal(EventStatus.Completed, (await read.Events.SingleAsync()).Status);
            Assert.Equal(QuestStatus.Completed, (await read.Quests.SingleAsync()).Status);
            var history = await read.EventStatusHistory.SingleAsync();
            Assert.Equal(EventStatus.Active, history.Previous);
            Assert.Equal(EventStatus.Completed, history.Next);
            var saved = await read.ScheduledWork.SingleAsync();
            Assert.Equal(WorkStatus.Processing, saved.Status);
            Assert.Equal(lease.Token, saved.LeaseId);
            Assert.Equal(replay ? 1 : 2, saved.Attempts);
            Assert.Equal(scenario.Clock.Now, saved.DueUtc);
            Assert.Equal(work.PayloadJson, saved.PayloadJson);
            Assert.Empty(await read.OutboxMessages.ToListAsync());
            Assert.Equal(scenario.Clock.Now, Assert.Single(quests.Calls).Now);
        }
        Assert.True(await scenario.Queue.CompleteAsync(lease));
        await using var completed = scenario.Database.CreateContext();
        Assert.Equal(WorkStatus.Completed, (await completed.ScheduledWork.SingleAsync()).Status);
    }

    /// <summary>Administrator replay cannot complete an Event before its immutable cutoff or at an obsolete boundary after extension.</summary>
    /// <param name="stale">Whether the Event was extended rather than replayed one tick before its unchanged end.</param>
    /// <returns>A task completing after unchanged parent/child state, immutable payload, and no-cascade assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShiftedReplayRetainsEarlyAndStaleBoundaryGuards(bool stale)
    {
        await using var scenario = await DeliveryScenario.CreateAsync();
        var end = TimeRules.EventWindow(scenario.Seed.Event.StartDate, scenario.Seed.Event.EndDate, scenario.Seed.Event.TimeZoneId).End;
        var work = Work(scenario.Seed.Event.Id, end);
        work.Status = WorkStatus.DeadLetter;
        work.Attempts = 8;
        await FoundationSeed.PersistAsync(scenario.Database, work, new Administrator { UserId = scenario.Seed.User.Id });
        if (stale)
        {
            await using var update = scenario.Database.CreateContext();
            (await update.Events.SingleAsync()).EndDate = scenario.Seed.Event.EndDate.AddDays(1);
            await update.SaveChangesAsync();
        }
        scenario.Clock.Now = stale ? end.AddMinutes(1) : end.AddTicks(-1);
        await scenario.Service.ReplayAsync(work.Id, "scheduled");
        var quests = new RecordingQuestLifecycle();
        var handler = new EventCompletionHandler(scenario.Factory, quests, scenario.Clock);
        if (stale)
            await handler.ExecuteAsync(work.Id, default);
        else
            Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
                handler.ExecuteAsync(work.Id, default))).Code);
        await using var read = scenario.Database.CreateContext();
        Assert.Equal(EventStatus.Active, (await read.Events.SingleAsync()).Status);
        Assert.Equal(QuestStatus.Active, (await read.Quests.SingleAsync()).Status);
        Assert.Empty(await read.EventStatusHistory.ToListAsync());
        Assert.Empty(quests.Calls);
        var saved = await read.ScheduledWork.SingleAsync();
        Assert.Equal(scenario.Clock.Now, saved.DueUtc);
        Assert.Equal(work.PayloadJson, saved.PayloadJson);
        Assert.Equal(WorkStatus.Pending, saved.Status);
        Assert.Null(saved.LeaseId);
    }

    private static ScheduledWork Work(Guid eventId, DateTimeOffset end) => new()
    {
        Type = WorkTypes.EventCompletion, DeduplicationKey = $"test-completion:{Guid.NewGuid():N}", DueUtc = end,
        PayloadJson = JsonSerializer.Serialize(new EventCompletionPayload(1, eventId, end))
    };
}
