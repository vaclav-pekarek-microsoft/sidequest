using System.Data;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks validation, inclusive state projection, and caller-owned transaction semantics of request reservations on real SQL.</summary>
/// <param name="database">The class-owned migrated catalog, never a shared development database.</param>
public sealed class MembershipRequestReservationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Missing and weaker transactions cannot silently acquire a short-lived reservation.</summary>
    /// <param name="isolation">Null for no explicit transaction, otherwise a weaker isolation level.</param>
    /// <returns>Completion after the precise precondition failure with unchanged tracking and transaction ownership.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task InvalidTransaction_IsRejected(IsolationLevel? isolation)
    {
        await using var db = database.CreateContext();
        await using var transaction = isolation is null ? null : await db.BeginTransactionAsync(isolation.Value);
        ISidequestDbContext boundary = db;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            boundary.ReadMembershipRequestStateForUpdateAsync(Guid.NewGuid(), Guid.NewGuid(), FoundationSeed.Now));
        Assert.Equal("An explicit Serializable transaction is required before reserving membership requests.", error.Message);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Empty Event or account identifiers fail before querying SQL or validating transaction ownership.</summary>
    /// <param name="emptyEvent">Whether the invalid identifier addresses the Event rather than the account.</param>
    /// <returns>Completion after exact validation-field and no-tracking assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyIdentifier_IsRejected(bool emptyEvent)
    {
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<DomainException>(() => db.ReadMembershipRequestStateForUpdateAsync(
            emptyEvent ? Guid.Empty : Guid.NewGuid(), emptyEvent ? Guid.NewGuid() : Guid.Empty, FoundationSeed.Now));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal(emptyEvent ? "eventId" : "userId", error.Field);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }

    /// <summary>Cancellation takes precedence over invalid identifiers and transaction validation.</summary>
    /// <returns>Completion after the original token is observed without any transaction or tracked writes.</returns>
    [Fact]
    public async Task PreCancelled_DoesNotQuery()
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            db.ReadMembershipRequestStateForUpdateAsync(Guid.Empty, Guid.Empty, FoundationSeed.Now, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }

    /// <summary>Every request state counts at the inclusive cutoff; old pending requests still deduplicate without leaking other pairs into the count.</summary>
    /// <param name="status">The lifecycle state counted independently of its pending semantics.</param>
    /// <returns>Completion after exact pending/history facts, no tracking or implicit save, and rollback of a saved request.</returns>
    [Theory]
    [InlineData(MembershipRequestStatus.Pending)]
    [InlineData(MembershipRequestStatus.Approved)]
    [InlineData(MembershipRequestStatus.Rejected)]
    [InlineData(MembershipRequestStatus.Withdrawn)]
    public async Task StateRead_IsScopedInclusiveAndCallerOwned(MembershipRequestStatus status)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var otherEvent = FoundationSeed.NewEvent(seed.Other.Id);
        await FoundationSeed.PersistAsync(database, otherEvent);
        await FoundationSeed.PersistAsync(database,
            Request(seed.Event.Id, seed.User.Id, FoundationSeed.Now.AddTicks(-1), MembershipRequestStatus.Withdrawn),
            Request(seed.Event.Id, seed.User.Id, FoundationSeed.Now, status),
            Request(seed.Event.Id, seed.User.Id, FoundationSeed.Now.AddTicks(1), MembershipRequestStatus.Rejected),
            Request(otherEvent.Id, seed.User.Id, FoundationSeed.Now, MembershipRequestStatus.Pending),
            Request(seed.Event.Id, seed.Other.Id, FoundationSeed.Now, MembershipRequestStatus.Pending));
        var unsaved = FoundationSeed.NewUser();
        var inserted = Request(seed.Event.Id, seed.User.Id, FoundationSeed.Now.AddMinutes(1), MembershipRequestStatus.Withdrawn);
        await using (var db = database.CreateContext())
        {
            db.Users.Add(unsaved);
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(seed.Event.Id);
            var state = await db.ReadMembershipRequestStateForUpdateAsync(seed.Event.Id, seed.User.Id, FoundationSeed.Now);
            Assert.Equal(new MembershipRequestState(status == MembershipRequestStatus.Pending, 2), state);
            Assert.Equal(new MembershipRequestState(status == MembershipRequestStatus.Pending, 0),
                await db.ReadMembershipRequestStateForUpdateAsync(seed.Event.Id, seed.User.Id, FoundationSeed.Now.AddHours(2)));
            var tracked = Assert.Single(db.ChangeTracker.Entries());
            Assert.Same(unsaved, tracked.Entity);
            Assert.Equal(EntityState.Added, tracked.State);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            Assert.Empty(unsaved.Version);
            db.MembershipRequests.Add(inserted);
            await db.SaveChangesAsync();
            Assert.Equal(3, (await db.ReadMembershipRequestStateForUpdateAsync(seed.Event.Id, seed.User.Id, FoundationSeed.Now)).RecentRequestCount);
            await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        Assert.False(await read.Users.AnyAsync(x => x.Id == unsaved.Id));
        Assert.False(await read.MembershipRequests.AnyAsync(x => x.Id == inserted.Id));
        Assert.Equal(3, await read.MembershipRequests.CountAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.User.Id));
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id));
    }

    private static EventMembershipRequest Request(Guid eventId, Guid userId, DateTimeOffset created, MembershipRequestStatus status) =>
        new() { EventId = eventId, UserId = userId, CreatedUtc = created, Status = status };
}
