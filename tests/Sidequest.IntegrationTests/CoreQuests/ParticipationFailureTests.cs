using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreBoundaries;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Checks authorization ordering, durable deadline reconciliation and atomic failure/cancellation through composed SQL participation services.</summary>
/// <param name="database">The uniquely owned migrated SQL catalog for independent scenarios.</param>
public sealed class ParticipationFailureTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Current eligibility, membership and private access are checked before any participation read or reservation.</summary>
    /// <param name="denial">The persisted authorization fact that excludes an otherwise known Event-owner actor.</param>
    /// <returns>Completion after safe denial, no participation query and unchanged Quest/delivery state.</returns>
    [Theory]
    [InlineData("missing-invitation")]
    [InlineData("revoked-invitation")]
    [InlineData("removed-membership")]
    [InlineData("disabled")]
    [InlineData("departed")]
    public async Task UnauthorizedActor_DoesNotReachParticipationReservation(string denial)
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var gate = new ParticipationMutationGate();
        var service = ParticipationTestServices.Service(scenario, scenario.Seed.Other, gate.Commands);
        await using (var setup = database.CreateContext())
        {
            if (denial != "missing-invitation")
                setup.QuestInvitations.Add(new QuestInvitation
                {
                    QuestId = scenario.Seed.Quest.Id,
                    UserId = scenario.Seed.Other.Id,
                    InvitedById = scenario.Seed.User.Id,
                    Status = denial == "revoked-invitation" ? QuestInvitationStatus.Revoked : QuestInvitationStatus.Active
                });
            if (denial == "removed-membership")
                (await setup.EventMemberships.SingleAsync(x => x.EventId == scenario.Seed.Event.Id && x.UserId == scenario.Seed.Other.Id))
                    .Status = MembershipStatus.Removed;
            if (denial == "disabled")
                (await setup.Users.SingleAsync(x => x.Id == scenario.Seed.Other.Id)).IsEligible = false;
            if (denial == "departed")
                (await setup.Users.SingleAsync(x => x.Id == scenario.Seed.Other.Id)).DepartureVerifiedUtc = scenario.Clock.GetUtcNow();
            await setup.SaveChangesAsync();
        }
        await using var before = database.CreateContext();
        var version = (await before.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).Version;
        var error = await Assert.ThrowsAsync<DomainException>(() => service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join));
        Assert.Equal(denial is "disabled" or "departed" ? ErrorCode.Forbidden : ErrorCode.NotFound, error.Code);
        Assert.False(gate.Session.Task.IsCompleted);
        await AssertUnchangedAsync(scenario, version, null);
    }

    /// <summary>A valid Joined actor cannot Follow; the reserved retained row and every durable side effect remain unchanged.</summary>
    /// <returns>Completion after exact conflict, reservation reachability, rowversion/state and absent delivery assertions.</returns>
    [Fact]
    public async Task FollowWhileJoined_PreservesRetainedRowAndCalendar()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var row = await SeedParticipationAsync(scenario, ParticipationStatus.Joined);
        var gate = new ParticipationMutationGate();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            ParticipationTestServices.Service(scenario, scenario.Seed.Other, gate.Commands)
                .ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Follow));
        Assert.Equal(ErrorCode.Conflict, error.Code);
        Assert.Equal("You already joined this Quest and receive attendee updates.", error.Message);
        Assert.True(gate.ReservationStarted.Task.IsCompletedSuccessfully);
        await AssertUnchangedAsync(scenario, scenario.Seed.Quest.Version, row);
    }

    /// <summary>Leaving or unfollowing with no retained row is a true no-op even though the missing key is reserved.</summary>
    /// <param name="command">The withdrawal command that cannot create participation from None.</param>
    /// <returns>Completion after reservation reachability and unchanged rowversion, calendar, audit and outbox assertions.</returns>
    [Theory]
    [InlineData(ParticipationCommand.Leave)]
    [InlineData(ParticipationCommand.Unfollow)]
    public async Task AbsentWithdrawal_DoesNotInsertOrEmitChanges(ParticipationCommand command)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var gate = new ParticipationMutationGate();
        await ParticipationTestServices.Service(scenario, scenario.Seed.Other, gate.Commands)
            .ParticipateAsync(scenario.Seed.Quest.Id, command);
        Assert.True(gate.ReservationStarted.Task.IsCompletedSuccessfully);
        await AssertUnchangedAsync(scenario, scenario.Seed.Quest.Version, null);
    }

    /// <summary>Real Event/Quest reconciliation commits at the exact end before a late Join is rejected, without reaching participation reservation.</summary>
    /// <param name="parentEnded">Whether the parent Event also reaches its end, rather than only the child Quest.</param>
    /// <returns>Completion after exact lifecycle histories, unchanged calendar revision and no participation/delivery intent.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactDeadline_CommitsReconciliationBeforeRejectingJoin(bool parentEnded)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        scenario.Clock.Now = parentEnded
            ? TimeRules.EventWindow(scenario.Seed.Event.StartDate, scenario.Seed.Event.EndDate, scenario.Seed.Event.TimeZoneId).End
            : scenario.Seed.Quest.EndUtc;
        var gate = new ParticipationMutationGate();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            ParticipationTestServices.Service(scenario, scenario.Seed.Other, gate.Commands)
                .ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join));
        Assert.Equal(ErrorCode.Conflict, error.Code);
        // Reconciliation legitimately captures an audience; it must not reserve or mutate an actor's participation.
        Assert.False(gate.ReservationStarted.Task.IsCompleted);
        await using var read = database.CreateContext();
        var quest = await read.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.Equal(scenario.Seed.Quest.CalendarRevision, quest.CalendarRevision);
        var history = Assert.Single(await read.QuestStatusHistory.Where(x => x.QuestId == quest.Id).ToListAsync());
        Assert.Equal(QuestStatus.Active, history.Previous);
        Assert.Equal(QuestStatus.Completed, history.Next);
        Assert.Equal(scenario.Clock.GetUtcNow(), history.OccurredUtc);
        Assert.Null(history.ActorId);
        Assert.Equal("Status:Active->Completed", (await read.AuditEntries.SingleAsync(x => x.ResourceId == quest.Id)).Action);
        Assert.Equal(parentEnded ? EventStatus.Completed : EventStatus.Active,
            (await read.Events.SingleAsync(x => x.Id == quest.EventId)).Status);
        var parentHistory = await read.EventStatusHistory.Where(x => x.EventId == quest.EventId).ToListAsync();
        if (parentEnded)
            Assert.Equal(EventStatus.Completed, Assert.Single(parentHistory).Next);
        else
            Assert.Empty(parentHistory);
        Assert.Empty(await read.Participations.Where(x => x.QuestId == quest.Id).ToListAsync());
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == quest.Id).ToListAsync());
    }

    /// <summary>Failure or cooperative cancellation after the actual SQL save rolls back participation, Quest revision, audit and outbox together.</summary>
    /// <param name="initial">Null for insertion or the retained state before an update.</param>
    /// <param name="cancel">Whether the injected post-save failure cancels the caller's token.</param>
    /// <returns>Completion after the original failure, exact unsaved durable state, and one successful explicit command after reservation release.</returns>
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(ParticipationStatus.Following, false)]
    [InlineData(ParticipationStatus.Following, true)]
    [InlineData(ParticipationStatus.Joined, false)]
    [InlineData(ParticipationStatus.Joined, true)]
    public async Task PostSaveFailure_RollsBackParticipationAuditOutboxAndCalendar(ParticipationStatus? initial, bool cancel)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var row = initial is null ? null : await SeedParticipationAsync(scenario, initial.Value);
        var command = initial == ParticipationStatus.Joined ? ParticipationCommand.Leave : ParticipationCommand.Join;
        using var cancellation = new CancellationTokenSource();
        Exception failure = cancel ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("Controlled participation failure.");
        var observer = new FailAfterParticipationSave(failure, cancel ? cancellation : null);
        Assert.Same(failure, await Record.ExceptionAsync(() =>
            ParticipationTestServices.Service(scenario, scenario.Seed.Other, observer)
                .ParticipateAsync(scenario.Seed.Quest.Id, command, cancellation.Token)));
        Assert.Equal(1, observer.Saves);
        Assert.Equal(cancel, cancellation.IsCancellationRequested);
        await AssertUnchangedAsync(scenario, scenario.Seed.Quest.Version, row);
        await ParticipationTestServices.Service(scenario).ParticipateAsync(scenario.Seed.Quest.Id, command);
        await using var read = database.CreateContext();
        var saved = Assert.Single(await read.Participations.Where(x => x.QuestId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.Equal(command == ParticipationCommand.Join ? ParticipationStatus.Joined : ParticipationStatus.None, saved.Status);
        if (row is not null)
            Assert.Equal(row.Id, saved.Id);
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.Single(await read.OutboxMessages.Where(x => x.AggregateId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.Equal(scenario.Seed.Quest.CalendarRevision + 1,
            (await read.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).CalendarRevision);
    }

    /// <summary>A composed Join blocked on another Event's cold participation reservation honors cancellation without saving or replaying the action.</summary>
    /// <returns>Completion after a DMV-observed participation wait, cancellation, no durable effects and a later explicit successful Join.</returns>
    [Fact]
    public async Task BlockedJoin_CancelsWithoutSavingAndReleasesItsEventLock()
    {
        var owned = new SqlTestDatabase();
        try
        {
            await owned.InitializeAsync();
            var first = await QuestScenario.CreateAsync(owned);
            var scenario = await QuestScenario.CreateAsync(owned);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            await using var holder = owned.CreateContext();
            await using var held = await holder.BeginTransactionAsync();
            await holder.LockEventAsync(first.Seed.Event.Id);
            Assert.Null(await holder.FindQuestParticipationForUpdateAsync(first.Seed.Quest.Id, first.Seed.Other.Id));
            var gate = new ParticipationMutationGate();
            gate.Release.TrySetResult();
            var attempt = ParticipationTestServices.Service(scenario, scenario.Seed.Other, gate, gate.Commands)
                .ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join, cancellation.Token);
            try
            {
                var waiter = await gate.Session.Task.WaitAsync(deadline.Token);
                var holderId = ((SqlConnection)holder.Database.GetDbConnection()).ServerProcessId;
                await SqlBoundaryCoordinator.WaitForBlockAsync(owned, waiter, holderId, deadline.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
                Assert.False(gate.Ready.Task.IsCompleted);
            }
            finally
            {
                cancellation.Cancel();
                await held.RollbackAsync();
                await Record.ExceptionAsync(() => attempt);
            }
            await AssertUnchangedAsync(scenario, scenario.Seed.Quest.Version, null);
            await ParticipationTestServices.Service(scenario).ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join, deadline.Token);
            await using var read = owned.CreateContext();
            Assert.Equal(ParticipationStatus.Joined, (await read.Participations.SingleAsync()).Status);
            Assert.Single(await read.AuditEntries.ToListAsync());
            Assert.Single(await read.OutboxMessages.ToListAsync());
        }
        finally
        {
            await owned.DisposeAsync();
        }
    }

    private static async Task<QuestParticipation> SeedParticipationAsync(QuestScenario scenario, ParticipationStatus initial)
    {
        var row = new QuestParticipation
        {
            QuestId = scenario.Seed.Quest.Id,
            UserId = scenario.Seed.Other.Id,
            Status = initial,
            ChangedUtc = scenario.Clock.GetUtcNow().AddDays(-1)
        };
        await FoundationSeed.PersistAsync(scenario.Database, row);
        return row;
    }

    private static async Task AssertUnchangedAsync(QuestScenario scenario, byte[] version, QuestParticipation? row)
    {
        await using var read = scenario.Database.CreateContext();
        var quest = await read.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id);
        Assert.Equal(version, quest.Version);
        Assert.Equal(scenario.Seed.Quest.CalendarRevision, quest.CalendarRevision);
        Assert.Equal(scenario.Seed.Quest.UpdatedUtc, quest.UpdatedUtc);
        var rows = await read.Participations.Where(x => x.QuestId == quest.Id).ToListAsync();
        if (row is null)
            Assert.Empty(rows);
        else
        {
            var current = Assert.Single(rows);
            Assert.Equal(row.Id, current.Id);
            Assert.Equal(row.Status, current.Status);
            Assert.Equal(row.Version, current.Version);
            Assert.Equal(row.ChangedUtc, current.ChangedUtc);
        }
        Assert.Empty(await read.AuditEntries.Where(x => x.ResourceId == quest.Id).ToListAsync());
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == quest.Id).ToListAsync());
    }

    private sealed class FailAfterParticipationSave(Exception failure, CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        internal int Saves { get; private set; }

        /// <inheritdoc />
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            Assert.Equal(4, result);
            Assert.Equal(EntityState.Unchanged, Assert.Single(db.ChangeTracker.Entries<QuestParticipation>()).State);
            Assert.Equal(EntityState.Unchanged, Assert.Single(db.ChangeTracker.Entries<Quest>()).State);
            Assert.Equal(EntityState.Unchanged, Assert.Single(db.ChangeTracker.Entries<AuditEntry>()).State);
            Assert.Equal(EntityState.Unchanged, Assert.Single(db.ChangeTracker.Entries<OutboxMessage>()).State);
            Saves++;
            cancellation?.Cancel();
            throw failure;
        }
    }
}
