using System.Data;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks the provider-owned identity lookup's validation, tracked results, exact key scope, and transaction ownership on SQL.</summary>
/// <param name="database">The uniquely owned migrated catalog for these boundary checks.</param>
public sealed class UserIdentityReservationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Missing and weaker transactions cannot silently acquire a short-lived identity reservation.</summary>
    /// <param name="isolation">Null for no explicit transaction, otherwise a weaker isolation level.</param>
    /// <returns>Completion after the exact precondition failure and unchanged context tracking and transaction ownership.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task InvalidTransaction_IsRejected(IsolationLevel? isolation)
    {
        await using var db = database.CreateContext();
        await using var transaction = isolation is null ? null : await db.BeginTransactionAsync(isolation.Value);
        ISidequestDbContext boundary = db;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.FindUserForUpdateAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("An explicit Serializable transaction is required before reserving an account identity.", error.Message);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Same(transaction, db.Database.CurrentTransaction);
    }

    /// <summary>Empty identifiers fail before querying SQL or validating transaction ownership.</summary>
    /// <param name="emptyTenant">Whether the invalid identifier is the tenant rather than the external account object.</param>
    /// <returns>Completion after exact validation-field assertions and absence of tracked writes.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyIdentityPart_IsRejected(bool emptyTenant)
    {
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            db.FindUserForUpdateAsync(emptyTenant ? Guid.Empty : Guid.NewGuid(), emptyTenant ? Guid.NewGuid() : Guid.Empty));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal(emptyTenant ? "tenantId" : "objectId", error.Field);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }

    /// <summary>Cancellation takes precedence over invalid identifiers and transaction validation.</summary>
    /// <returns>Completion after observing the original cancellation token without tracking or starting a transaction.</returns>
    [Fact]
    public async Task PreCancelled_DoesNotQuery()
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            db.FindUserForUpdateAsync(Guid.Empty, Guid.Empty, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }

    /// <summary>The exact tenant/object pair returns tracked current fields or null, never grants access, and leaves save/rollback to the caller.</summary>
    /// <param name="existing">Whether the requested pair exists alongside same-tenant and same-object decoys.</param>
    /// <returns>Completion after exact identity/eligibility/rowversion assertions and rollback of a saved insert or update.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactIdentity_IsTrackedWithoutImplicitSaveOrGrant(bool existing)
    {
        var target = FoundationSeed.NewUser();
        target.IsEligible = false;
        target.DepartureVerifiedUtc = FoundationSeed.Now;
        var otherTenant = FoundationSeed.NewUser();
        otherTenant.ObjectId = target.ObjectId;
        var otherObject = FoundationSeed.NewUser();
        otherObject.TenantId = target.TenantId;
        await FoundationSeed.PersistAsync(database, otherTenant, otherObject);
        if (existing)
            await FoundationSeed.PersistAsync(database, target);
        var unsaved = FoundationSeed.NewUser();
        await using (var db = database.CreateContext())
        {
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            db.Users.Add(unsaved);
            await using var transaction = await db.BeginTransactionAsync();
            var found = await db.FindUserForUpdateAsync(target.TenantId, target.ObjectId);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            Assert.Empty(unsaved.Version);
            Assert.Equal(existing ? 2 : 1, db.ChangeTracker.Entries().Count());
            if (existing)
            {
                Assert.NotNull(found);
                Assert.Equal(target.Id, found.Id);
                Assert.Equal(target.TenantId, found.TenantId);
                Assert.Equal(target.ObjectId, found.ObjectId);
                Assert.Equal(target.Version, found.Version);
                Assert.Equal(target.DisplayName, found.DisplayName);
                Assert.Equal(target.Email, found.Email);
                Assert.False(found.IsEligible);
                Assert.Equal(FoundationSeed.Now, found.DepartureVerifiedUtc);
                Assert.Equal(EntityState.Unchanged, db.Entry(found).State);
                found.DisplayName = "Caller-owned update";
            }
            else
            {
                Assert.Null(found);
                db.Users.Add(target);
            }
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        Assert.False(await read.Users.AnyAsync(x => x.Id == unsaved.Id));
        var final = await read.Users.SingleOrDefaultAsync(x => x.Id == target.Id);
        if (existing)
        {
            Assert.NotNull(final);
            Assert.Equal(target.DisplayName, final.DisplayName);
            Assert.Equal(target.Version, final.Version);
        }
        else
            Assert.Null(final);
        Assert.False(await read.Administrators.AnyAsync(x => x.UserId == target.Id));
    }
}
