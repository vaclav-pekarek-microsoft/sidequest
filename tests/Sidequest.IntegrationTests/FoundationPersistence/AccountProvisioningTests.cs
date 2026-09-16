using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreBoundaries;
using Sidequest.Web.Authentication;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks trusted admission, current eligibility, cancellation, independent identities, and atomic bootstrap through real SQL provisioning.</summary>
/// <param name="database">The uniquely owned migrated SQL fixture for this class.</param>
public sealed class AccountProvisioningTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Invalid admission cannot reach the identity reservation, persist an account, or bootstrap an administrator.</summary>
    /// <param name="invalid">The invalid tenant, object, role, authentication, or synthetic-mode partition.</param>
    /// <returns>Completion after safe Forbidden, no identity query, no retry, and absent account assertions.</returns>
    [Theory]
    [InlineData("tenant")]
    [InlineData("object")]
    [InlineData("role")]
    [InlineData("anonymous")]
    [InlineData("synthetic")]
    public async Task UnadmittedIdentity_NeverReachesReservation(string invalid)
    {
        var context = new ProvisioningTestContext(database);
        var principal = context.Principal(context.BootstrapObjectId);
        var identity = (ClaimsIdentity)principal.Identity!;
        if (invalid == "anonymous")
            principal = new(new ClaimsIdentity(principal.Claims));
        else if (invalid == "synthetic")
            identity.AddClaim(new(FoundationAuthenticationSettings.SyntheticClaim, "true"));
        else
        {
            var type = invalid switch { "tenant" => "tid", "object" => "oid", _ => "roles" };
            identity.RemoveClaim(identity.FindFirst(type)!);
            identity.AddClaim(new(type, invalid switch { "tenant" => Guid.NewGuid().ToString(), "object" => Guid.Empty.ToString(), _ => "Administrator" }));
        }
        var gate = new UserIdentityReadGate();
        gate.Release.TrySetResult();
        var error = await Assert.ThrowsAsync<DomainException>(() => context.Accounts(gate).ProvisionAsync(principal, default));
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Equal("This identity is not eligible for Sidequest.", error.Message);
        Assert.False(gate.Started.Task.IsCompleted);
        Assert.Empty(context.Warnings);
        await using var read = database.CreateContext();
        Assert.False(await read.Users.AnyAsync(x => x.TenantId == context.TenantId));
    }

    /// <summary>A sign-in waiting behind a persisted disable or verified departure reads the committed denial instead of overwriting it.</summary>
    /// <param name="departed">Whether the concurrent change verifies departure rather than clearing eligibility.</param>
    /// <returns>Completion after observed SQL blocking, Forbidden without retries, unchanged contact/version, and no bootstrap grant.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingSignIn_RechecksCommittedIneligibility(bool departed)
    {
        var context = new ProvisioningTestContext(database);
        var user = Account(context, context.BootstrapObjectId);
        await FoundationSeed.PersistAsync(database, user);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var holder = database.CreateContext();
        await using var transaction = await holder.BeginTransactionAsync();
        var persisted = (await holder.FindUserForUpdateAsync(user.TenantId, user.ObjectId))!;
        if (departed)
            persisted.DepartureVerifiedUtc = FoundationSeed.Now;
        else
            persisted.IsEligible = false;
        await holder.SaveChangesAsync();
        var holderId = ((SqlConnection)holder.Database.GetDbConnection()).ServerProcessId;
        var gate = new UserIdentityReadGate();
        gate.Release.TrySetResult();
        var signIn = context.Accounts(gate).ProvisionAsync(context.Principal(user.ObjectId), deadline.Token);
        try
        {
            await SqlBoundaryCoordinator.WaitForBlockAsync(database, await gate.Started.Task.WaitAsync(deadline.Token), holderId, deadline.Token);
            await transaction.CommitAsync();
            var error = await Assert.ThrowsAsync<DomainException>(() => signIn);
            Assert.Equal(ErrorCode.Forbidden, error.Code);
            Assert.Equal("This local account is disabled. Contact an administrator.", error.Message);
        }
        finally
        {
            deadline.Cancel();
            await transaction.DisposeAsync();
            await Record.ExceptionAsync(() => signIn);
        }
        await using var read = database.CreateContext();
        var final = await read.Users.SingleAsync(x => x.Id == user.Id);
        Assert.Equal(departed, final.IsEligible);
        Assert.Equal(departed ? FoundationSeed.Now : null, final.DepartureVerifiedUtc);
        Assert.Equal(user.DisplayName, final.DisplayName);
        Assert.Equal(user.Email, final.Email);
        Assert.Equal(user.LastSignedInUtc, final.LastSignedInUtc);
        Assert.Equal(persisted.Version, final.Version);
        Assert.False(await read.Administrators.AnyAsync(x => x.UserId == user.Id));
        Assert.Empty(context.Warnings);
    }

    /// <summary>Cancelling an identity-lock wait aborts provisioning without retries, partial accounts, or administrator grants.</summary>
    /// <param name="existing">Whether the held key is an existing account or an absent identity range.</param>
    /// <returns>Completion after an observed lock wait, caller cancellation, rollback release, and one fresh explicit successful sign-in.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedSignIn_CancellationDoesNotWriteOrRetry(bool existing)
    {
        var context = new ProvisioningTestContext(database);
        var user = Account(context, context.BootstrapObjectId);
        if (existing)
            await FoundationSeed.PersistAsync(database, user);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var holder = database.CreateContext();
        await using var transaction = await holder.BeginTransactionAsync();
        await holder.FindUserForUpdateAsync(user.TenantId, user.ObjectId);
        var holderId = ((SqlConnection)holder.Database.GetDbConnection()).ServerProcessId;
        var gate = new UserIdentityReadGate();
        gate.Release.TrySetResult();
        var signIn = context.Accounts(gate).ProvisionAsync(context.Principal(user.ObjectId), cancellation.Token);
        try
        {
            await SqlBoundaryCoordinator.WaitForBlockAsync(database, await gate.Started.Task.WaitAsync(deadline.Token), holderId, deadline.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
            await transaction.RollbackAsync();
        }
        finally
        {
            cancellation.Cancel();
            await transaction.DisposeAsync();
            await Record.ExceptionAsync(() => signIn);
        }
        await using (var read = database.CreateContext())
        {
            var stored = await read.Users.SingleOrDefaultAsync(x => x.TenantId == user.TenantId && x.ObjectId == user.ObjectId);
            if (existing)
            {
                Assert.NotNull(stored);
                Assert.Equal(user.Version, stored.Version);
                Assert.Equal(user.LastSignedInUtc, stored.LastSignedInUtc);
            }
            else
                Assert.Null(stored);
            Assert.False(await read.Administrators.AnyAsync(x => x.UserId == user.Id));
        }
        Assert.Empty(context.Warnings);
        await context.Accounts().ProvisionAsync(context.Principal(user.ObjectId), deadline.Token);
        await using var final = database.CreateContext();
        var account = await final.Users.SingleAsync(x => x.TenantId == context.TenantId);
        Assert.Equal(FoundationSeed.Now, account.LastSignedInUtc);
        Assert.Equal(!existing, await final.Administrators.AnyAsync(x => x.UserId == account.Id));
    }

    /// <summary>Reserving one existing identity does not globally block another admitted identity's contact update.</summary>
    /// <returns>Completion after an independent sign-in commits while the first reservation remains owned and unchanged.</returns>
    [Fact]
    public async Task ExistingDifferentIdentity_UpdatesWhileAnotherIsReserved()
    {
        var context = new ProvisioningTestContext(database);
        var held = Account(context, context.BootstrapObjectId);
        var independent = Account(context, Guid.NewGuid());
        await FoundationSeed.PersistAsync(database, held, independent);
        await using var holder = database.CreateContext();
        await using var transaction = await holder.BeginTransactionAsync();
        await holder.FindUserForUpdateAsync(held.TenantId, held.ObjectId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await context.Accounts().ProvisionAsync(context.Principal(independent.ObjectId), deadline.Token);
        Assert.Same(transaction, holder.Database.CurrentTransaction);
        await using var read = database.CreateContext();
        Assert.Equal(held.Version, (await read.Users.SingleAsync(x => x.Id == held.Id)).Version);
        var updated = await read.Users.SingleAsync(x => x.Id == independent.Id);
        Assert.Equal("First sign-in", updated.DisplayName);
        Assert.Equal("first@sample.invalid", updated.Email);
        Assert.Equal(FoundationSeed.Now, updated.LastSignedInUtc);
        Assert.NotEqual(independent.Version, updated.Version);
        Assert.False(await read.Administrators.AnyAsync(x => x.UserId == held.Id || x.UserId == independent.Id));
        Assert.Empty(context.Warnings);
    }

    /// <summary>A failure after SQL saves a first account and bootstrap grant rolls both back; later sign-ins never restore a removed grant.</summary>
    /// <returns>Completion after verified saved entries, rollback, explicit first provisioning, role removal, and an unprivileged later update.</returns>
    [Fact]
    public async Task Bootstrap_IsAtomicAndNeverRegranted()
    {
        var context = new ProvisioningTestContext(database);
        var failure = new FailAfterBootstrapSave();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Accounts(failure).ProvisionAsync(context.Principal(context.BootstrapObjectId), default));
        Assert.Equal("Injected failure after bootstrap save.", error.Message);
        Assert.NotEqual(Guid.Empty, failure.SavedUserId);
        Assert.Empty(context.Warnings);
        await using (var read = database.CreateContext())
        {
            Assert.False(await read.Users.AnyAsync(x => x.TenantId == context.TenantId));
            Assert.False(await read.Administrators.AnyAsync(x => x.UserId == failure.SavedUserId));
        }
        await context.Accounts().ProvisionAsync(context.Principal(context.BootstrapObjectId), default);
        UserAccount first;
        await using (var revoke = database.CreateContext())
        {
            first = await revoke.Users.SingleAsync(x => x.TenantId == context.TenantId);
            revoke.Administrators.Remove(await revoke.Administrators.SingleAsync(x => x.UserId == first.Id));
            await revoke.SaveChangesAsync();
        }
        await context.Accounts(now: FoundationSeed.Now.AddMinutes(1))
            .ProvisionAsync(context.Principal(context.BootstrapObjectId, "Later sign-in", "later@sample.invalid"), default);
        await using var final = database.CreateContext();
        var account = await final.Users.SingleAsync(x => x.TenantId == context.TenantId);
        Assert.Equal(first.Id, account.Id);
        Assert.Equal("Later sign-in", account.DisplayName);
        Assert.Equal("later@sample.invalid", account.Email);
        Assert.Equal(FoundationSeed.Now.AddMinutes(1), account.LastSignedInUtc);
        Assert.NotEqual(first.Version, account.Version);
        Assert.False(await final.Administrators.AnyAsync(x => x.UserId == account.Id));
        Assert.False(await final.EventMemberships.AnyAsync(x => x.UserId == account.Id));
        Assert.False(await final.EventOwners.AnyAsync(x => x.UserId == account.Id));
    }

    private static UserAccount Account(ProvisioningTestContext context, Guid objectId) => new()
    {
        TenantId = context.TenantId, ObjectId = objectId, DisplayName = "Before sign-in",
        Email = "before@sample.invalid", LastSignedInUtc = FoundationSeed.Now.AddDays(-1)
    };

    private sealed class FailAfterBootstrapSave : SaveChangesInterceptor
    {
        internal Guid SavedUserId { get; private set; }

        /// <inheritdoc />
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            var user = Assert.Single(db.ChangeTracker.Entries<UserAccount>());
            var administrator = Assert.Single(db.ChangeTracker.Entries<Administrator>());
            Assert.Equal(2, result);
            Assert.Equal(EntityState.Unchanged, user.State);
            Assert.Equal(EntityState.Unchanged, administrator.State);
            Assert.Equal(user.Entity.Id, administrator.Entity.UserId);
            SavedUserId = user.Entity.Id;
            throw new InvalidOperationException("Injected failure after bootstrap save.");
        }
    }
}
