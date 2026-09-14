using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks save/transaction contracts, cancellation, rowversion conflicts, and immutable-history enforcement across overloads.</summary>
/// <param name="database">The isolated migrated SQL fixture used for independent writer and reader contexts.</param>
public sealed class SaveChangesContractTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Checks that an empty save reports zero changes without tracking rows or leaving an active transaction.</summary>
    /// <param name="explicitToken">Whether to pass an explicit noncancelled token instead of the default argument.</param>
    /// <returns>A task completing after empty-context save and side-effect assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesAsync_EmptyContext_ReturnsZero(bool explicitToken)
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        Assert.Equal(0, explicitToken ? await db.SaveChangesAsync(cancellation.Token) : await db.SaveChangesAsync());
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }

    /// <summary>Checks that inserting one account reports one change and supplies the SQL-generated eight-byte rowversion.</summary>
    /// <param name="explicitToken">Whether to pass an explicit noncancelled cancellation token.</param>
    /// <returns>A task completing after tracked-state and fresh stored-account assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesAsync_AddedEntity_ReturnsCountAndGeneratedVersion(bool explicitToken)
    {
        var row = FoundationSeed.NewUser();
        await using (var db = database.CreateContext())
        {
            db.Users.Add(row);
            Assert.Empty(row.Version);
            using var cancellation = new CancellationTokenSource();
            Assert.Equal(1, explicitToken ? await db.SaveChangesAsync(cancellation.Token) : await db.SaveChangesAsync());
            Assert.Equal(EntityState.Unchanged, db.Entry(row).State);
        }
        await using var read = database.CreateContext();
        var stored = await read.Users.SingleAsync(x => x.Id == row.Id);
        Assert.Equal(8, stored.Version.Length);
        Assert.Equal("Synthetic Ada", stored.DisplayName);
        Assert.Equal("ada@example.invalid", stored.Email);
        Assert.Equal(row.TenantId, stored.TenantId);
        Assert.Equal(row.ObjectId, stored.ObjectId);
    }

    /// <summary>Checks that every save entry point inserts durably while honoring automatic versus explicitly deferred tracker acceptance.</summary>
    /// <param name="overload">The synchronous/default/token or explicit bool save selector.</param>
    /// <param name="accept">Whether the selected overload must automatically accept the successfully inserted entity.</param>
    /// <returns>A task completing after independent SQL inspection, tracker acceptance, and a zero-write subsequent save.</returns>
    [Theory]
    [InlineData("sync", true)]
    [InlineData("syncTrue", true)]
    [InlineData("syncFalse", false)]
    [InlineData("token", true)]
    [InlineData("asyncTrue", true)]
    [InlineData("asyncFalse", false)]
    public async Task SaveOverloads_Success_PersistsInsertAndHonorsAcceptance(string overload, bool accept)
    {
        var user = FoundationSeed.NewUser();
        await using var db = database.CreateContext();
        db.Users.Add(user);
        Assert.Equal(EntityState.Added, db.Entry(user).State);
        Assert.Empty(user.Version);

        Assert.Equal(1, await SaveAsync(db, overload));
        Assert.Equal(accept ? EntityState.Unchanged : EntityState.Added, db.Entry(user).State);
        Assert.Equal(!accept, db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        var stored = await read.Users.SingleAsync(x => x.Id == user.Id);
        Assert.Equal("Synthetic Ada", stored.DisplayName);
        Assert.Equal("ada@example.invalid", stored.Email);
        Assert.Equal(user.TenantId, stored.TenantId);
        Assert.Equal(user.ObjectId, stored.ObjectId);
        Assert.Equal(8, stored.Version.Length);

        if (!accept)
        {
            Assert.Equal(EntityState.Added, db.Entry(user).State);
            db.ChangeTracker.AcceptAllChanges();
        }
        Assert.Equal(EntityState.Unchanged, db.Entry(user).State);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(stored.Version, user.Version);
        Assert.Equal(0, await SaveAsync(db, overload));
        Assert.Equal(EntityState.Unchanged, db.Entry(user).State);
        await using var final = database.CreateContext();
        var unchanged = await final.Users.SingleAsync(x => x.Id == user.Id);
        Assert.Equal("Synthetic Ada", unchanged.DisplayName);
        Assert.Equal(stored.Version, unchanged.Version);
    }

    /// <summary>Checks that a stale update or delete reports domain Conflict rather than overwriting the winning SQL row.</summary>
    /// <param name="kind">The Event, Quest, or Participation entity whose rowversion is raced.</param>
    /// <param name="delete">Whether the losing writer attempts deletion instead of an update.</param>
    /// <returns>A task completing after both writes and fresh winner-content/rowversion assertions.</returns>
    [Theory]
    [InlineData("Event", false)]
    [InlineData("Quest", false)]
    [InlineData("Participation", false)]
    [InlineData("Participation", true)]
    public async Task SaveChangesAsync_StaleRowversion_ThrowsConflictAndPreservesWinner(string kind, bool delete)
    {
        await CheckConcurrencyAsync(kind, delete, "token");
    }

    /// <summary>Checks default Serializable and explicitly requested ReadCommitted transactions through the production abstraction.</summary>
    /// <param name="readCommitted">Whether to request ReadCommitted instead of using the default isolation level.</param>
    /// <returns>A task completing after actual transaction-isolation/identity assertions and rollback.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginTransactionAsync_UsesRequestedIsolation(bool readCommitted)
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        await using var transaction = readCommitted
            ? await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellation.Token)
            : await db.BeginTransactionAsync(cancellationToken: cancellation.Token);
        Assert.Equal(readCommitted ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            transaction.GetDbTransaction().IsolationLevel);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        await transaction.RollbackAsync();
    }

    /// <summary>Checks cancellation propagation without an active transaction or tracked SQL work.</summary>
    /// <returns>A task completing after the expected cancellation and context-side-effect assertions.</returns>
    [Fact]
    public async Task BeginTransactionAsync_PreCancelledToken_DoesNotOpenUsableTransaction()
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.BeginTransactionAsync(cancellationToken: cancellation.Token));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Gets Audit, Event history, and Quest history cases for both attempted modification and deletion.</summary>
    public static TheoryData<string, bool> Histories => new()
    {
        { "Audit", false }, { "Audit", true }, { "Event", false }, { "Event", true }, { "Quest", false }, { "Quest", true }
    };

    /// <summary>Gets every history/mutation combination for both asynchronous accept-all-changes overload arguments.</summary>
    public static TheoryData<string, bool, bool> AsyncHistories
    {
        get
        {
            var data = new TheoryData<string, bool, bool>();
            foreach (var kind in new[] { "Audit", "Event", "Quest" })
                foreach (var delete in new[] { false, true })
                    foreach (var accept in new[] { false, true })
                        data.Add(kind, delete, accept);
            return data;
        }
    }

    /// <summary>Gets each immutable history kind with the synchronous default, true, and false save-overload selectors.</summary>
    public static TheoryData<string, string> SyncHistories
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var kind in new[] { "Audit", "Event", "Quest" })
                foreach (var overload in new[] { "sync", "syncTrue", "syncFalse" })
                    data.Add(kind, overload);
            return data;
        }
    }

    /// <summary>Checks immutable-history rejection and atomic rejection of an unrelated insert through the token-based save.</summary>
    /// <param name="kind">The Audit, Event, or Quest history kind.</param>
    /// <param name="delete">Whether to delete the history rather than replace its reason.</param>
    /// <returns>A task completing after the exact Conflict and durable unchanged-state assertions.</returns>
    [Theory]
    [MemberData(nameof(Histories))]
    public Task AuditHistory_SaveChangesAsync_RejectsMutation(string kind, bool delete) =>
        CheckImmutableAsync(kind, delete, "token");

    /// <summary>Checks that asynchronous bool overloads must not bypass immutable-history or unrelated-write atomicity guards.</summary>
    /// <param name="kind">The Audit, Event, or Quest history kind.</param>
    /// <param name="delete">Whether the forbidden operation deletes rather than modifies the record.</param>
    /// <param name="accept">The acceptAllChangesOnSuccess argument supplied to the inherited save overload.</param>
    /// <returns>A task completing after durable side-effect and expected Conflict assertions.</returns>
    [Theory]
    [MemberData(nameof(AsyncHistories))]
    public Task AuditHistory_SaveChangesAsyncWithAcceptAllChanges_RejectsMutation(string kind, bool delete, bool accept) =>
        CheckImmutableAsync(kind, delete, accept ? "asyncTrue" : "asyncFalse");

    /// <summary>Checks that synchronous save overloads cannot delete immutable history or commit a neighboring staged insert.</summary>
    /// <param name="kind">The Audit, Event, or Quest history kind.</param>
    /// <param name="overload">The sync, syncTrue, or syncFalse save selector.</param>
    /// <returns>A task completing after fresh SQL observations and required Conflict assertions.</returns>
    [Theory]
    [MemberData(nameof(SyncHistories))]
    public Task AuditHistory_SynchronousSave_RejectsDeletion(string kind, string overload) =>
        CheckImmutableAsync(kind, true, overload);

    /// <summary>Checks that synchronous save overloads cannot rewrite history reasons or commit an unrelated staged insert.</summary>
    /// <param name="kind">The Audit, Event, or Quest history kind.</param>
    /// <param name="overload">The sync, syncTrue, or syncFalse save selector.</param>
    /// <returns>A task completing after fresh SQL observations and required Conflict assertions.</returns>
    [Theory]
    [MemberData(nameof(SyncHistories))]
    public Task AuditHistory_SynchronousSave_RejectsModification(string kind, string overload) =>
        CheckImmutableAsync(kind, false, overload);

    /// <summary>Checks consistent domain Conflict translation across inherited save overloads while SQL preserves the original or winning row.</summary>
    /// <param name="overload">The synchronous or asynchronous bool save-overload selector.</param>
    /// <param name="concurrency">True for a stale rowversion write; false for a duplicate tenant/object insertion.</param>
    /// <returns>A task completing after preserved SQL-state and exact domain error assertions.</returns>
    [Theory]
    [InlineData("sync", false)]
    [InlineData("syncTrue", false)]
    [InlineData("syncFalse", false)]
    [InlineData("asyncTrue", false)]
    [InlineData("asyncFalse", false)]
    [InlineData("sync", true)]
    [InlineData("syncTrue", true)]
    [InlineData("syncFalse", true)]
    [InlineData("asyncTrue", true)]
    [InlineData("asyncFalse", true)]
    public async Task SaveOverloads_DuplicateAndConcurrency_TranslateToConflict(string overload, bool concurrency)
    {
        if (concurrency)
        {
            await CheckConcurrencyAsync("Event", false, overload);
            return;
        }
        var user = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, user);
        var duplicate = FoundationSeed.NewUser();
        duplicate.TenantId = user.TenantId;
        duplicate.ObjectId = user.ObjectId;
        Exception? error;
        await using (var db = database.CreateContext())
        {
            db.Users.Add(duplicate);
            error = await Record.ExceptionAsync(() => SaveAsync(db, overload));
        }
        await using var read = database.CreateContext();
        var stored = await read.Users.SingleAsync(x => x.TenantId == user.TenantId && x.ObjectId == user.ObjectId);
        Assert.Equal(user.Id, stored.Id);
        Assert.Equal("ada@example.invalid", stored.Email);
        Assert.Equal(user.Version, stored.Version);
        FoundationSeed.AssertConflict(error, FoundationSeed.DuplicateMessage);
    }

    private async Task CheckImmutableAsync(string kind, bool delete, string overload)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        Entity history = kind switch
        {
            "Audit" => new AuditEntry
            {
                ResourceKind = ResourceKind.Quest, ResourceId = seed.Quest.Id, ActorId = seed.Other.Id,
                Action = "Published", Reason = "Original history", CorrelationId = "foundation-correlation",
                OccurredUtc = FoundationSeed.Now
            },
            "Event" => new EventStatusHistory
            {
                EventId = seed.Event.Id, Previous = EventStatus.Draft, Next = EventStatus.Active,
                ActorId = seed.Other.Id, Reason = "Original history", OccurredUtc = FoundationSeed.Now
            },
            "Quest" => new QuestStatusHistory
            {
                QuestId = seed.Quest.Id, Previous = QuestStatus.Draft, Next = QuestStatus.Active,
                ActorId = seed.Other.Id, Reason = "Original history", OccurredUtc = FoundationSeed.Now
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        await FoundationSeed.PersistAsync(database, history);
        var unrelated = FoundationSeed.NewUser();
        Exception? error;
        await using (var db = database.CreateContext())
        {
            var loaded = (Entity)(await db.FindAsync(history.GetType(), history.Id))!;
            if (delete)
                db.Remove(loaded);
            else
                db.Entry(loaded).Property("Reason").CurrentValue = "Forbidden replacement";
            db.Users.Add(unrelated);
            error = await Record.ExceptionAsync(() => SaveAsync(db, overload));
        }
        await using var read = database.CreateContext();
        var stored = (Entity?)await read.FindAsync(history.GetType(), history.Id);
        var sideEffect = await read.Users.AnyAsync(x => x.Id == unrelated.Id);
        // Include durable observations in the failure instead of losing them to an early Throws assertion.
        Assert.True(error is not null,
            $"Immutable contract bypass via {overload}: historyPresent={stored is not null}, " +
            $"reason={(stored is null ? "<deleted>" : read.Entry(stored).Property("Reason").CurrentValue)}, unrelatedUserPersisted={sideEffect}.");
        FoundationSeed.AssertConflict(error, "History records are immutable.");
        Assert.NotNull(stored);
        Assert.Equal("Original history", read.Entry(stored).Property("Reason").CurrentValue);
        Assert.Equal(seed.Other.Id, read.Entry(stored).Property("ActorId").CurrentValue);
        Assert.Equal(FoundationSeed.Now, read.Entry(stored).Property("OccurredUtc").CurrentValue);
        Assert.Equal(history.Version, stored.Version);
        Assert.False(sideEffect);
    }

    private async Task CheckConcurrencyAsync(string kind, bool delete, string overload)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        Entity row = kind switch
        {
            "Event" => seed.Event,
            "Quest" => seed.Quest,
            "Participation" => new QuestParticipation
            {
                QuestId = seed.Quest.Id, UserId = seed.User.Id, Status = ParticipationStatus.Following,
                ChangedUtc = FoundationSeed.Now
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (row is QuestParticipation)
            await FoundationSeed.PersistAsync(database, row);
        var property = kind switch { "Event" => "Name", "Quest" => "Title", _ => "Status" };
        object winnerValue = kind == "Participation" ? ParticipationStatus.Joined : "Winner text";
        object loserValue = kind == "Participation" ? ParticipationStatus.None : "Stale overwrite";
        Exception? error;
        await using (var first = database.CreateContext())
        await using (var second = database.CreateContext())
        {
            var winner = (Entity)(await first.FindAsync(row.GetType(), row.Id))!;
            var loser = (Entity)(await second.FindAsync(row.GetType(), row.Id))!;
            first.Entry(winner).Property(property).CurrentValue = winnerValue;
            await first.SaveChangesAsync();
            Assert.NotEqual(row.Version, winner.Version);
            if (delete)
                second.Remove(loser);
            else
                second.Entry(loser).Property(property).CurrentValue = loserValue;
            error = await Record.ExceptionAsync(() => SaveAsync(second, overload));
        }
        await using var read = database.CreateContext();
        var stored = (Entity)(await read.FindAsync(row.GetType(), row.Id))!;
        Assert.NotNull(stored);
        Assert.Equal(winnerValue, read.Entry(stored).Property(property).CurrentValue);
        Assert.NotEqual(row.Version, stored.Version);
        FoundationSeed.AssertConflict(error, FoundationSeed.ConcurrencyMessage);
    }

    private static Task<int> SaveAsync(SidequestDbContext db, string overload) => overload switch
    {
        "token" => db.SaveChangesAsync(CancellationToken.None),
        "asyncTrue" => db.SaveChangesAsync(true, CancellationToken.None),
        "asyncFalse" => db.SaveChangesAsync(false, CancellationToken.None),
        "sync" => Task.FromResult(db.SaveChanges()),
        "syncTrue" => Task.FromResult(db.SaveChanges(true)),
        "syncFalse" => Task.FromResult(db.SaveChanges(false)),
        _ => throw new ArgumentOutOfRangeException(nameof(overload))
    };

    /// <summary>Checks that an update racing a committed deletion reports Conflict and does not recreate the missing membership.</summary>
    /// <returns>A task completing after deleted-row absence and unchanged-parent assertions.</returns>
    [Fact]
    public async Task SaveChangesAsync_AlreadyDeletedRow_ThrowsConflictWithoutResurrection()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var relation = seed.Membership();
        await FoundationSeed.PersistAsync(database, relation);
        await using (var stale = database.CreateContext())
        {
            var loaded = await stale.EventMemberships.SingleAsync(x => x.Id == relation.Id);
            await using (var winner = database.CreateContext())
            {
                winner.Remove(await winner.EventMemberships.SingleAsync(x => x.Id == relation.Id));
                Assert.Equal(1, await winner.SaveChangesAsync());
            }
            loaded.Status = MembershipStatus.Removed;
            FoundationSeed.AssertConflict(await Record.ExceptionAsync(() => stale.SaveChangesAsync()), FoundationSeed.ConcurrencyMessage);
        }
        await using var read = database.CreateContext();
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id));
        Assert.Equal(seed.Event.Version, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Version);
    }

    /// <summary>Checks that an already-cancelled save inserts no account and generates no rowversion.</summary>
    /// <returns>A task completing after cancellation and fresh SQL absence assertions.</returns>
    [Fact]
    public async Task SaveChangesAsync_PreCancelledToken_PersistsNoStagedUser()
    {
        var user = FoundationSeed.NewUser();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using (var db = database.CreateContext())
        {
            db.Users.Add(user);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cancellation.Token));
        }
        await using var read = database.CreateContext();
        Assert.False(await read.Users.AnyAsync(x => x.Id == user.Id));
        Assert.Empty(user.Version);
    }

    /// <summary>Checks cancellation forwarding for both asynchronous bool arguments without accepting or persisting the pending insert.</summary>
    /// <param name="accept">The requested acceptance policy, which must not run after a cancelled save.</param>
    /// <returns>A task completing after cancellation, retained Added state, empty version, and independent SQL absence assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesAsync_BoolOverloadPreCancelled_RetainsAddedWithoutDurableWrite(bool accept)
    {
        var user = FoundationSeed.NewUser();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var db = database.CreateContext();
        db.Users.Add(user);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => db.SaveChangesAsync(accept, cancellation.Token));
        Assert.Equal(EntityState.Added, db.Entry(user).State);
        Assert.True(db.ChangeTracker.HasChanges());
        Assert.Empty(user.Version);
        Assert.Null(db.Database.CurrentTransaction);
        await using var read = database.CreateContext();
        Assert.False(await read.Users.AnyAsync(x => x.Id == user.Id));
        Assert.False(await read.Users.AnyAsync(x => x.TenantId == user.TenantId && x.ObjectId == user.ObjectId));
    }
}
