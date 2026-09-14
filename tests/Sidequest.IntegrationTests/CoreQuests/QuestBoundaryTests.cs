using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Exercises civil-time boundaries, retained-state guards, ownership continuity, and cancelled operation rollback.</summary>
/// <param name="database">Existing isolated migrated-SQL fixture.</param>
public sealed class QuestBoundaryTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Prague spring gaps and unresolved autumn overlaps are rejected; explicitly chosen offsets select distinct instants.</summary>
    /// <param name="month">DST transition month.</param>
    /// <param name="day">DST transition day.</param>
    /// <param name="offsetHours">Explicit overlap offset, or null to require ambiguity validation.</param>
    /// <param name="valid">Whether this civil-time mapping is accepted.</param>
    /// <returns>Completion after exact UTC mapping or no-partial-creation assertions.</returns>
    [Theory]
    [InlineData(3, 29, null, false)]
    [InlineData(3, 29, 1, false)]
    [InlineData(10, 25, null, false)]
    [InlineData(10, 25, 1, true)]
    [InlineData(10, 25, 2, true)]
    [InlineData(10, 25, 3, false)]
    public async Task LocalTime_DstGapAndOverlap_RequireExplicitValidMapping(int month, int day, int? offsetHours, bool valid)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        await using (var db = database.CreateContext())
        {
            var parent = await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id);
            parent.StartDate = new DateOnly(2026, month, day);
            parent.EndDate = parent.StartDate;
            await db.SaveChangesAsync();
        }
        scenario.Clock.Now = new DateTimeOffset(2026, month, day, 0, 0, 0, TimeSpan.Zero);
        var input = scenario.Input() with
        {
            StartLocal = new DateTime(2026, month, day, 2, 30, 0),
            EndLocal = new DateTime(2026, month, day, 4, 0, 0),
            StartOffset = offsetHours is null ? null : TimeSpan.FromHours(offsetHours.Value)
        };
        if (valid)
        {
            var id = await scenario.Service().CreateAsync(scenario.Seed.Event.Id, input);
            var detail = await scenario.Service().GetAsync(id);
            Assert.Equal(new DateTimeOffset(2026, month, day, 2, 30, 0, TimeSpan.FromHours(offsetHours!.Value)).ToUniversalTime(),
                detail.Summary.StartUtc);
        }
        else
        {
            Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
                scenario.Service().CreateAsync(scenario.Seed.Event.Id, input))).Code);
            await using var read = database.CreateContext();
            Assert.Single(await read.Quests.Where(q => q.EventId == scenario.Seed.Event.Id).ToListAsync());
            Assert.False(await read.AuditEntries.AnyAsync(a => a.ActorId == scenario.Seed.User.Id));
        }
    }

    /// <summary>Quest intervals may exactly touch inclusive Event boundaries but never extend a single tick beyond them.</summary>
    /// <param name="edge">Start or end boundary.</param>
    /// <param name="outside">Whether the interval exceeds the valid window by one tick.</param>
    /// <returns>Completion after containment acceptance or precise validation failure.</returns>
    [Theory]
    [InlineData("start", false)]
    [InlineData("start", true)]
    [InlineData("end", false)]
    [InlineData("end", true)]
    public async Task Containment_UsesExactExclusiveEnd(string edge, bool outside)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var input = scenario.Input();
        if (edge == "start")
            input = input with { StartLocal = new DateTime(2026, 7, 15).AddTicks(outside ? -1 : 0) };
        else
            input = input with { EndLocal = new DateTime(2026, 7, 17).AddTicks(outside ? 1 : 0) };
        if (outside)
            Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
                scenario.Service().CreateAsync(scenario.Seed.Event.Id, input))).Code);
        else
        {
            var id = await scenario.Service().CreateAsync(scenario.Seed.Event.Id, input);
            var summary = (await scenario.Service().GetAsync(id)).Summary;
            Assert.Equal(edge == "start" ? new DateTimeOffset(2026, 7, 14, 22, 0, 0, TimeSpan.Zero) : FoundationSeed.Now,
                summary.StartUtc);
            Assert.Equal(edge == "end" ? new DateTimeOffset(2026, 7, 16, 22, 0, 0, TimeSpan.Zero) : FoundationSeed.Now.AddHours(2),
                summary.EndUtc);
        }
    }

    /// <summary>The exact end instant stops new participation even with late jobs; retained Joined users can still leave.</summary>
    /// <param name="deltaTicks">Clock displacement before, at, or after the exact Quest end.</param>
    /// <returns>Completion after effective status, command guard, and leave-state assertions.</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EffectiveEnd_BlocksJoiningAtBoundary_ButAllowsLeave(long deltaTicks)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        await service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
        scenario.Clock.Now = scenario.Seed.Quest.EndUtc.AddTicks(deltaTicks);
        var summary = (await service.GetAsync(scenario.Seed.Quest.Id)).Summary;
        Assert.Equal(deltaTicks < 0 ? QuestStatus.Active : QuestStatus.Completed, summary.Status);
        if (deltaTicks >= 0)
        {
            Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
                scenario.Service(scenario.Seed.Other).ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join))).Code);
            await using var read = database.CreateContext();
            Assert.Equal(QuestStatus.Completed, (await read.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id)).Status);
        }
        else
            await scenario.Service(scenario.Seed.Other).ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
        await service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Leave);
        Assert.Equal(ParticipationStatus.None, (await service.GetAsync(scenario.Seed.Quest.Id)).Summary.Participation);
    }

    /// <summary>Draft creation stops exactly at the Event's inclusive local date window end, independently of stored Active status.</summary>
    /// <returns>Completion after exact lifecycle conflict and unchanged Quest count assertions.</returns>
    [Fact]
    public async Task ParentEffectiveEnd_RejectsNewDraftWithoutWaitingForJob()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        scenario.Clock.Now = TimeRules.EventWindow(scenario.Seed.Event.StartDate, scenario.Seed.Event.EndDate,
            scenario.Seed.Event.TimeZoneId).End;
        scenario.Reconciler.OnReconcileAsync = async (context, eventId, _, token) =>
        {
            (await context.Events.SingleAsync(e => e.Id == eventId, token)).Status = EventStatus.Completed;
            return true;
        };
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().CreateAsync(scenario.Seed.Event.Id, scenario.Input()))).Code);
        await using var db = database.CreateContext();
        Assert.Single(await db.Quests.Where(x => x.EventId == scenario.Seed.Event.Id).ToListAsync());
        Assert.Equal(EventStatus.Completed, (await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status);
        Assert.Equal(1, scenario.Reconciler.Calls);
    }

    /// <summary>Owner targets must be eligible current Event members; creator metadata cannot bypass the last-eligible-owner guard.</summary>
    /// <returns>Completion after target denial, equal-owner self-removal, and role-loss assertions.</returns>
    [Fact]
    public async Task Ownership_RequiresEligibleMembers_AndRetainsLastOwnerWithoutCreatorPrivilege()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = scenario.Seed.Quest.Id;
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            service.RemoveOwnerAsync(id, scenario.Seed.User.Id))).Code);
        var outsider = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, outsider);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() => service.AddOwnerAsync(id, outsider.Id))).Code);
        await service.AddOwnerAsync(id, scenario.Seed.Other.Id);
        await service.AddOwnerAsync(id, scenario.Seed.Other.Id);
        await service.RemoveOwnerAsync(id, scenario.Seed.User.Id);
        Assert.False((await service.GetAsync(id)).Summary.IsOwner);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => service.AddOwnerAsync(id, scenario.Seed.User.Id))).Code);
        Assert.Equal(scenario.Seed.Other.Id, Assert.Single((await scenario.Service(scenario.Seed.Other).GetAsync(id)).Owners).Id);
        Assert.Equal(ParticipationStatus.None, (await scenario.Service(scenario.Seed.Other).GetAsync(id)).Summary.Participation);
    }

    /// <summary>Concurrent equal owners removing themselves can never commit an ownerless Quest.</summary>
    /// <returns>Completion after one success, explicit losing conflict, and exact surviving owner assertions.</returns>
    [Fact]
    public async Task ConcurrentOwnerRemovals_RetainOneEligibleOwner()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        await scenario.Service().AddOwnerAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id);
        var attempts = await Task.WhenAll(
            Record.ExceptionAsync(() => scenario.Service().RemoveOwnerAsync(scenario.Seed.Quest.Id, scenario.Seed.User.Id)),
            Record.ExceptionAsync(() => scenario.Service(scenario.Seed.Other).RemoveOwnerAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id)));
        Assert.Single(attempts, e => e is null);
        var rejected = Assert.Single(attempts, e => e is not null);
        await using var db = database.CreateContext();
        var remaining = Assert.Single(await db.QuestOwners.Where(o => o.QuestId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.Contains(remaining.UserId, new[] { scenario.Seed.User.Id, scenario.Seed.Other.Id });
        Assert.True(rejected is DomainException, $"Expected explicit conflict, not {rejected?.GetType().Name}.");
        Assert.Equal(ErrorCode.Conflict, Assert.IsType<DomainException>(rejected).Code);
    }

    /// <summary>Pre-cancelled commands cannot partially create draft, owner, audit, or work records.</summary>
    /// <returns>Completion after cancellation and unchanged persisted rows.</returns>
    [Fact]
    public async Task Cancellation_PreventsAnyPartialCreate()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        using var token = new CancellationTokenSource();
        await token.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scenario.Service().CreateAsync(scenario.Seed.Event.Id, scenario.Input(), token.Token));
        await using var db = database.CreateContext();
        Assert.Single(await db.Quests.Where(q => q.EventId == scenario.Seed.Event.Id).ToListAsync());
        Assert.False(await db.AuditEntries.AnyAsync(a => a.ActorId == scenario.Seed.User.Id));
        Assert.False(await db.OutboxMessages.AnyAsync(x => x.AggregateId == scenario.Seed.Quest.Id));
    }
}
