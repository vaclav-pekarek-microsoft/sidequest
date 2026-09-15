using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Exercises the shared Event-first lock and Event reconciliation port through real Quest operations and caller-owned SQL transactions.</summary>
/// <param name="database">Existing isolated migrated-SQL fixture.</param>
public sealed class QuestCoordinationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Actual publication reserves completion work before inserting rather than converting compatible shared gap locks at save time.</summary>
    /// <returns>Completion after a blocked reservation, absence of attempted schedule inserts, and one successful explicit publication after release.</returns>
    [Fact]
    public async Task Publication_ReservesCompletionRangeBeforeAttemptingInsert()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        string version;
        await using (var prepare = database.CreateContext())
        {
            var quest = await prepare.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id);
            quest.Status = QuestStatus.Draft;
            await prepare.SaveChangesAsync();
            version = Convert.ToBase64String(quest.Version);
        }
        var prefix = $"quest-complete:{scenario.Seed.Quest.Id:N}:{scenario.Seed.Quest.EndUtc.UtcTicks}";
        await using var blocker = database.CreateContext();
        await using var reservation = await blocker.BeginTransactionAsync();
        Assert.False(await blocker.HasPendingScheduledWorkForUpdateAsync(prefix));

        var observer = new ScheduleReservationObserver();
        var service = new QuestService(new ObservedContextFactory(database, observer),
            new ResourceAccess(StubCurrentUser.For(scenario.Seed.User)), new ChangeWriter(), scenario.Clock, scenario.Reconciler);
        var failure = await Assert.ThrowsAsync<SqlException>(() =>
            service.ChangeStatusAsync(scenario.Seed.Quest.Id, version, QuestStatus.Active, ""));
        Assert.Equal(1222, failure.Number);
        Assert.True(observer.ReservationReadAttempted);
        Assert.False(observer.ScheduleInsertAttempted);
        await reservation.RollbackAsync();

        await service.ChangeStatusAsync(scenario.Seed.Quest.Id, version, QuestStatus.Active, "");
        await using var read = database.CreateContext();
        Assert.Equal(QuestStatus.Active, (await read.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).Status);
        var scheduled = Assert.Single(await read.ScheduledWork.Where(x => x.QuestId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.Equal(WorkStatus.Pending, scheduled.Status);
        Assert.Equal(scenario.Seed.Quest.EndUtc, scheduled.DueUtc);
        Assert.Single(await read.QuestStatusHistory.Where(x => x.QuestId == scenario.Seed.Quest.Id).ToListAsync());
    }

    /// <summary>Event-owned completion and child cleanup commit before a late Join is rejected, without committing any participation.</summary>
    /// <returns>Completion after both lifecycle journals and absence of participation/delivery are asserted.</returns>
    [Fact]
    public async Task Reconciliation_CommitsParentAndChildCleanup_BeforeRejectingLateJoin()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        scenario.Clock.Now = TimeRules.EventWindow(scenario.Seed.Event.StartDate, scenario.Seed.Event.EndDate,
            scenario.Seed.Event.TimeZoneId).End;
        scenario.Reconciler.OnReconcileAsync = async (context, eventId, now, token) =>
        {
            var parent = await context.Events.SingleAsync(e => e.Id == eventId, token);
            context.EventStatusHistory.Add(new EventStatusHistory
            {
                EventId = eventId, Previous = parent.Status, Next = EventStatus.Completed,
                OccurredUtc = now, Reason = "Contract-controlled Event completion."
            });
            parent.Status = EventStatus.Completed;
            await new QuestEventLifecycle(new ChangeWriter()).CompleteForEventAsync(context, eventId, now, token);
            return true;
        };
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join))).Code);
        await using var db = database.CreateContext();
        Assert.Equal(EventStatus.Completed, (await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Completed, (await db.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id)).Status);
        Assert.Single(await db.EventStatusHistory.Where(h => h.EventId == scenario.Seed.Event.Id).ToListAsync());
        Assert.Single(await db.QuestStatusHistory.Where(h => h.QuestId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.False(await db.Participations.AnyAsync(p => p.QuestId == scenario.Seed.Quest.Id));
        Assert.False(await db.OutboxMessages.AnyAsync(o => o.AggregateId == scenario.Seed.Quest.Id));
        Assert.Equal(1, scenario.Reconciler.Calls);
    }

    /// <summary>A failed Event reconciliation propagates and rolls back staged parent/child lifecycle changes before the Quest command can run.</summary>
    /// <returns>Completion after unchanged aggregate states and absence of partial histories or participation.</returns>
    [Fact]
    public async Task ReconciliationFailure_RollsBackParentAndChild_WithoutRunningCommand()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        scenario.Reconciler.OnReconcileAsync = async (context, eventId, now, token) =>
        {
            (await context.Events.SingleAsync(e => e.Id == eventId, token)).Status = EventStatus.Cancelled;
            await new QuestEventLifecycle(new ChangeWriter()).CancelForEventAsync(context, eventId,
                null, "Synthetic parent cleanup failure.", now, token);
            throw new InvalidOperationException("Synthetic Event reconciliation failure.");
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Service().ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join));
        Assert.Equal("Synthetic Event reconciliation failure.", failure.Message);
        await using var db = database.CreateContext();
        Assert.Equal(EventStatus.Active, (await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Active, (await db.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id)).Status);
        Assert.False(await db.QuestStatusHistory.AnyAsync(h => h.QuestId == scenario.Seed.Quest.Id));
        Assert.False(await db.AuditEntries.AnyAsync(h => h.ResourceId == scenario.Seed.Quest.Id));
        Assert.False(await db.OutboxMessages.AnyAsync(o => o.AggregateId == scenario.Seed.Quest.Id));
        Assert.False(await db.Participations.AnyAsync(p => p.QuestId == scenario.Seed.Quest.Id));
    }

    /// <summary>Competing membership removal and Quest ownership assignment serialize on the same parent lock, never committing an owner without membership.</summary>
    /// <returns>Completion after one accepted operation, a precise losing domain error, and the cross-resource invariant.</returns>
    [Fact]
    public async Task MembershipRemovalRacingOwnerAssignment_PreservesCrossResourceContinuity()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var target = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, target);
        await FoundationSeed.PersistAsync(database, scenario.Seed.Membership(target.Id));

        async Task RemoveMembershipAsync()
        {
            await using var db = database.CreateContext();
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(scenario.Seed.Event.Id);
            if (await db.QuestOwners.AnyAsync(o => o.QuestId == scenario.Seed.Quest.Id && o.UserId == target.Id))
                throw new DomainException(ErrorCode.Conflict, "Remove Quest ownership before membership.");
            (await db.EventMemberships.SingleAsync(m => m.EventId == scenario.Seed.Event.Id && m.UserId == target.Id)).Status = MembershipStatus.Removed;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var attempts = await Task.WhenAll(
            Record.ExceptionAsync(RemoveMembershipAsync),
            Record.ExceptionAsync(() => scenario.Service().AddOwnerAsync(scenario.Seed.Quest.Id, target.Id)));
        Assert.Single(attempts, error => error is null);
        var rejected = Assert.IsType<DomainException>(Assert.Single(attempts, error => error is not null));
        Assert.Contains(rejected.Code, new[] { ErrorCode.Conflict, ErrorCode.Validation });
        await using var read = database.CreateContext();
        var member = await read.EventMemberships.SingleAsync(m => m.EventId == scenario.Seed.Event.Id && m.UserId == target.Id);
        var owner = await read.QuestOwners.AnyAsync(o => o.QuestId == scenario.Seed.Quest.Id && o.UserId == target.Id);
        Assert.Equal(owner ? MembershipStatus.Active : MembershipStatus.Removed, member.Status);
        Assert.Equal(owner ? ErrorCode.Conflict : ErrorCode.Validation, rejected.Code);
    }

    /// <summary>A Quest end crossed between transactions is durably reconciled before Join fails, not staged in the rejected command transaction.</summary>
    /// <returns>Completion after stored completion/history and absent participation/notification assertions.</returns>
    [Fact]
    public async Task QuestEndCrossedBetweenTransactions_CommitsCompletionBeforeJoinConflict()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var reads = 0;
        scenario.Clock.ReadUtc = () => Interlocked.Increment(ref reads) == 1
            ? scenario.Seed.Quest.EndUtc.AddTicks(-1) : scenario.Seed.Quest.EndUtc;
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join))).Code);
        await using var db = database.CreateContext();
        Assert.Equal(QuestStatus.Completed, (await db.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id)).Status);
        Assert.Single(await db.QuestStatusHistory.Where(h => h.QuestId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.False(await db.Participations.AnyAsync(p => p.QuestId == scenario.Seed.Quest.Id));
        Assert.False(await db.OutboxMessages.AnyAsync(o => o.AggregateId == scenario.Seed.Quest.Id));
    }

    /// <summary>An Event end crossed after preliminary reconciliation still commits parent cleanup before rejecting creation or participation.</summary>
    /// <param name="createDraft">Whether the rejected command creates a draft rather than joining an existing Quest.</param>
    /// <returns>Completion after committed parent state, repeated reconciliation and absence of a new draft.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EventEndCrossedBetweenTransactions_CommitsReconciliationBeforeCommandConflict(bool createDraft)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var end = TimeRules.EventWindow(scenario.Seed.Event.StartDate, scenario.Seed.Event.EndDate, scenario.Seed.Event.TimeZoneId).End;
        var reads = 0;
        scenario.Clock.ReadUtc = () => Interlocked.Increment(ref reads) == 1 ? end.AddTicks(-1) : end;
        scenario.Reconciler.OnReconcileAsync = async (context, eventId, now, token) =>
        {
            if (now < end)
                return false;
            var parent = await context.Events.SingleAsync(e => e.Id == eventId, token);
            context.EventStatusHistory.Add(new EventStatusHistory
            {
                EventId = eventId, Previous = parent.Status, Next = EventStatus.Completed,
                OccurredUtc = now, Reason = "Contract-controlled Event completion."
            });
            parent.Status = EventStatus.Completed;
            await new QuestEventLifecycle(new ChangeWriter()).CompleteForEventAsync(context, eventId, now, token);
            return true;
        };
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            createDraft
                ? scenario.Service().CreateAsync(scenario.Seed.Event.Id, scenario.Input())
                : scenario.Service().ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join))).Code);
        await using var db = database.CreateContext();
        Assert.Equal(EventStatus.Completed, (await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Completed, Assert.Single(await db.Quests.Where(q => q.EventId == scenario.Seed.Event.Id).ToListAsync()).Status);
        Assert.Single(await db.EventStatusHistory.Where(h => h.EventId == scenario.Seed.Event.Id).ToListAsync());
        Assert.Single(await db.QuestStatusHistory.Where(h => h.QuestId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.False(await db.Participations.AnyAsync(p => p.QuestId == scenario.Seed.Quest.Id));
        Assert.False(await db.OutboxMessages.AnyAsync(o => o.AggregateId == scenario.Seed.Quest.Id));
        Assert.Equal(2, scenario.Reconciler.Calls);
    }

    private sealed class ScheduleReservationObserver : DbCommandInterceptor
    {
        internal bool ReservationReadAttempted { get; private set; }
        internal bool ScheduleInsertAttempted { get; private set; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [ScheduledWork]", StringComparison.Ordinal))
                ReservationReadAttempted = true;
            if (command.CommandText.Contains("INSERT INTO [ScheduledWork]", StringComparison.Ordinal))
                ScheduleInsertAttempted = true;
            command.CommandText = "SET LOCK_TIMEOUT 500;\n" + command.CommandText;
            return ValueTask.FromResult(result);
        }
    }
}
