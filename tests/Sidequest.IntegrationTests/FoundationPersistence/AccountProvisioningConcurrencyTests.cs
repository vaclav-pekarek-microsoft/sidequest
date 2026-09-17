using Microsoft.EntityFrameworkCore;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreBoundaries;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Exercises admitted account provisioning against real SQL with controlled identity-read overlap and exact durable outcomes.</summary>
public sealed class AccountProvisioningConcurrencyTests
{
    /// <summary>Missing identities and existing-row updates reserve write intent before shared reads can force insert or update retries.</summary>
    /// <param name="existing">Whether the bootstrap candidate is already provisioned without an administrator grant.</param>
    /// <param name="sameIdentity">Whether both admitted sign-ins address the same tenant/object pair.</param>
    /// <returns>Completion after observed SQL contention, no retry warnings, exact account/contact versions, and one-time bootstrap assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompetingSignIns_ReserveIdentityAndCommitWithoutRetries(bool existing, bool sameIdentity)
    {
        var database = new SqlTestDatabase();
        try
        {
            await database.InitializeAsync();
            var context = new ProvisioningTestContext(database);
            var firstObject = context.BootstrapObjectId;
            var secondObject = sameIdentity ? firstObject : Guid.NewGuid();
            var original = new UserAccount
            {
                TenantId = context.TenantId, ObjectId = firstObject, DisplayName = "Before sign-in",
                Email = "before@sample.invalid", LastSignedInUtc = FoundationSeed.Now.AddDays(-1)
            };
            if (existing)
                await FoundationSeed.PersistAsync(database, original);
            await using (var before = database.CreateContext())
            {
                Assert.Equal(existing ? 1 : 0, await before.Users.CountAsync());
                Assert.Empty(await before.Administrators.ToListAsync());
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var firstGate = new UserIdentityReadGate();
            var secondGate = new UserIdentityReadGate();
            var firstSignIn = context.Accounts(firstGate).ProvisionAsync(context.Principal(firstObject), deadline.Token);
            Task secondSignIn = Task.CompletedTask;
            Task blocked = Task.CompletedTask;
            UserAccount? committedFirst = null;
            try
            {
                await firstGate.Read.Task.WaitAsync(deadline.Token);
                secondSignIn = context.Accounts(secondGate, FoundationSeed.Now.AddMinutes(1))
                    .ProvisionAsync(context.Principal(secondObject, "Second sign-in", "second@sample.invalid"), deadline.Token);
                var waiter = await secondGate.Started.Task.WaitAsync(deadline.Token);
                blocked = SqlBoundaryCoordinator.WaitForBlockAsync(database, waiter, await firstGate.Started.Task, observation.Token);
                var observed = await Task.WhenAny(blocked, secondGate.Read.Task).WaitAsync(deadline.Token);
                if (observed == blocked)
                {
                    await blocked;
                    firstGate.Release.TrySetResult();
                    await firstSignIn;
                    await secondGate.Read.Task.WaitAsync(deadline.Token);
                    await using var intermediate = database.CreateContext();
                    committedFirst = await intermediate.Users.AsNoTracking().SingleAsync(x => x.ObjectId == firstObject, deadline.Token);
                    Assert.Equal("First sign-in", committedFirst.DisplayName);
                    Assert.Equal("first@sample.invalid", committedFirst.Email);
                    Assert.Equal(FoundationSeed.Now, committedFirst.LastSignedInUtc);
                    Assert.Equal(existing ? 0 : 1, await intermediate.Administrators.CountAsync(deadline.Token));
                }
                // On the baseline both shared reads complete. Release them together to produce
                // the real SQL conversion deadlock, which the unchanged retry logger exposes.
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                await Task.WhenAll(firstSignIn, secondSignIn);
                Assert.Empty(context.Warnings);
                Assert.Same(blocked, observed);
            }
            finally
            {
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                deadline.Cancel();
                observation.Cancel();
                await Record.ExceptionAsync(() => Task.WhenAll(firstSignIn, secondSignIn, blocked));
            }
            Assert.NotNull(committedFirst);
            await using var read = database.CreateContext();
            var accounts = await read.Users.AsNoTracking().ToListAsync();
            Assert.Equal(sameIdentity ? 1 : 2, accounts.Count);
            var first = Assert.Single(accounts, x => x.ObjectId == firstObject);
            Assert.Equal(committedFirst.Id, first.Id);
            if (existing)
            {
                Assert.Equal(original.Id, first.Id);
                Assert.NotEqual(original.Version, committedFirst.Version);
            }
            var second = Assert.Single(accounts, x => x.ObjectId == secondObject);
            Assert.Equal("Second sign-in", second.DisplayName);
            Assert.Equal("second@sample.invalid", second.Email);
            Assert.Equal(FoundationSeed.Now.AddMinutes(1), second.LastSignedInUtc);
            if (sameIdentity)
                Assert.NotEqual(committedFirst.Version, second.Version);
            else
            {
                Assert.Equal(committedFirst.Version, first.Version);
                Assert.Equal("First sign-in", first.DisplayName);
                Assert.Equal("first@sample.invalid", first.Email);
                Assert.Equal(FoundationSeed.Now, first.LastSignedInUtc);
            }
            Assert.All(accounts, account =>
            {
                Assert.Equal(context.TenantId, account.TenantId);
                Assert.True(account.IsEligible);
                Assert.Null(account.DepartureVerifiedUtc);
                Assert.Equal(8, account.Version.Length);
            });
            var administrators = await read.Administrators.ToListAsync();
            if (existing)
                Assert.Empty(administrators);
            else
                Assert.Equal(first.Id, Assert.Single(administrators).UserId);
            Assert.Empty(await read.EventMemberships.ToListAsync());
            Assert.Empty(await read.EventOwners.ToListAsync());
        }
        finally
        {
            await database.DisposeAsync();
        }
    }
}
