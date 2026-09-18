using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Administration;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.SecondaryAdministration;

/// <summary>Real migrated-SQL administrator authorization, concurrency and last-eligible-administrator invariants.</summary>
public sealed class AdministratorTests
{
    /// <summary>Trusted local selection excludes foreign/departed accounts, additions are audited, duplicates conflict and removal never auto-restores.</summary>
    [Fact]
    public async Task TrustedSelectionAndExplicitAddRemoveAreAudited()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var choices = await s.Service.SearchAccountsAsync("Replacement");
        var target = Assert.Single(choices);
        Assert.Equal(s.Replacement.Id, target.Id);
        Assert.Equal(s.Replacement.Email, target.Email);
        await s.Service.AddAsync(target.Id, target.Version);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => s.Service.AddAsync(target.Id, target.Version))).Code);
        var assignments = await s.Service.ListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(s.Replacement.Email, assignments.Single(x => x.UserId == target.Id).Email);
        await s.Service.RemoveAsync(target.Id, assignments.Single(x => x.UserId == target.Id).Version);
        Assert.Single(await s.Service.ListAsync());
        await using var db = s.Database.CreateContext();
        Assert.Equal(new[] { "AdministratorAdded", "AdministratorRemoved" },
            await db.AuditEntries.OrderBy(x => x.Action).Select(x => x.Action).ToArrayAsync());
        Assert.All(await db.AuditEntries.ToArrayAsync(), x => Assert.Equal(s.Seed.User.Id, x.ActorId));
        Assert.Empty(await db.OutboxMessages.ToArrayAsync());
        Assert.Empty(await db.EventMemberships.Where(x => x.UserId == s.Replacement.Id).ToArrayAsync());
    }

    /// <summary>Wrong tenant, revoked actor eligibility, removed assignment and anonymous identity fail before any mutation.</summary>
    /// <param name="failure">Persisted or identity denial partition.</param>
    [Theory]
    [InlineData("anonymous")]
    [InlineData("wrong-tenant")]
    [InlineData("ineligible")]
    [InlineData("departed")]
    [InlineData("removed-role")]
    public async Task ActorRevocationDeniesAllAdministration(string failure)
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await using (var db = s.Database.CreateContext())
        {
            var actor = await db.Users.SingleAsync(x => x.Id == s.Seed.User.Id);
            if (failure == "ineligible") actor.IsEligible = false;
            if (failure == "departed") actor.DepartureVerifiedUtc = s.Clock.Now;
            if (failure == "removed-role") db.Administrators.Remove(await db.Administrators.SingleAsync());
            if (failure == "anonymous") s.Current.Identity = null;
            if (failure == "wrong-tenant") s.Current.Identity = s.Current.Identity! with { TenantId = Guid.NewGuid() };
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ListAsync())).Code);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.SearchAccountsAsync("Replacement"))).Code);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.AddAsync(s.Replacement.Id, s.Replacement.Version))).Code);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => s.Email.GetSettingsAsync())).Code);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Email.SaveSettingAsync(new(BusinessEmailRules.BrandKey, "Unauthorized brand", [])))).Code);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Email.SaveTemplateAsync(BusinessEmailRules.Default("quest.invitation")))).Code);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Email.PreviewAsync(BusinessEmailRules.Default("quest.invitation")))).Code);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => s.PreviewAsync())).Code);
        await using var verify = s.Database.CreateContext();
        Assert.Empty(await verify.AuditEntries.ToArrayAsync());
        Assert.Empty(await verify.OutboxMessages.ToArrayAsync());
    }

    /// <summary>A foreign tenant account cannot be added even with its genuine persisted ID and version.</summary>
    [Fact]
    public async Task CrossTenantSelectionAndAssignmentAreDenied()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var foreign = FoundationSeed.NewUser();
        foreign.DisplayName = "Replacement Foreign";
        await FoundationSeed.PersistAsync(s.Database, foreign);
        Assert.DoesNotContain(await s.Service.SearchAccountsAsync("Replacement"), x => x.Id == foreign.Id);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.AddAsync(foreign.Id, foreign.Version))).Code);
        await using var db = s.Database.CreateContext();
        Assert.Single(await db.Administrators.ToArrayAsync());
        Assert.Empty(await db.AuditEntries.ToArrayAsync());
    }

    /// <summary>Email-only searches project eligible same-tenant contacts without disclosing foreign, departed or disabled accounts.</summary>
    /// <returns>Completion after the real SQL projection and each account-eligibility filter is verified.</returns>
    [Fact]
    public async Task EmailSearchProjectsOnlyAuthorizedEligibleContacts()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await using (var db = s.Database.CreateContext())
        {
            (await db.Users.SingleAsync(x => x.Id == s.Replacement.Id)).Email = "contact-search@example.invalid";
            var foreign = FoundationSeed.NewUser();
            foreign.Email = "contact-search-foreign@example.invalid";
            var disabled = FoundationSeed.NewUser();
            disabled.TenantId = s.Seed.User.TenantId;
            disabled.IsEligible = false;
            disabled.Email = "contact-search-disabled@example.invalid";
            var departed = FoundationSeed.NewUser();
            departed.TenantId = s.Seed.User.TenantId;
            departed.DepartureVerifiedUtc = s.Clock.Now.AddDays(-1);
            departed.Email = "contact-search-departed@example.invalid";
            db.Users.AddRange(foreign, disabled, departed);
            await db.SaveChangesAsync();
        }
        var contact = Assert.Single(await s.Service.SearchAccountsAsync("contact-search"));
        Assert.Equal(s.Replacement.Id, contact.Id);
        Assert.Equal("contact-search@example.invalid", contact.Email);
        Assert.Equal("Replacement Person", contact.DisplayName);
        Assert.NotEmpty(contact.Version);
    }

    /// <summary>A stale selected account version cannot grant administration after a directory/local-account change.</summary>
    [Fact]
    public async Task StaleAccountSelectionDoesNotGrantAdministrator()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await using (var db = s.Database.CreateContext())
        {
            (await db.Users.SingleAsync(x => x.Id == s.Replacement.Id)).DisplayName = "Changed name";
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.AddAsync(s.Replacement.Id, s.Replacement.Version))).Code);
        await using var verify = s.Database.CreateContext();
        Assert.Single(await verify.Administrators.ToArrayAsync());
        Assert.Empty(await verify.AuditEntries.ToArrayAsync());
    }

    /// <summary>Two administrators concurrently removing themselves cannot both succeed; one eligible assignment and exactly one removal audit remain.</summary>
    [Fact]
    public async Task CompetingSelfRemovalsRetainLastEligibleAdministrator()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await s.Service.AddAsync(s.Replacement.Id, s.Replacement.Version);
        var assignments = await s.Service.ListAsync();
        var factory = new ObservedContextFactory(s.Database, new AdministratorReadBarrier());
        var first = new AdministrationService(factory, s.Access, new ChangeWriter(), s.Clock, s.Gate);
        var other = new AdministrationService(factory, new ResourceAccess(StubCurrentUser.For(s.Replacement)),
            new ChangeWriter(), s.Clock, s.Gate);
        var results = await Task.WhenAll(
            Record.ExceptionAsync(() => first.RemoveAsync(s.Seed.User.Id, assignments.Single(x => x.UserId == s.Seed.User.Id).Version)),
            Record.ExceptionAsync(() => other.RemoveAsync(s.Replacement.Id, assignments.Single(x => x.UserId == s.Replacement.Id).Version)));
        Assert.Single(results, x => x is null);
        Assert.Equal(ErrorCode.Conflict, Assert.IsType<DomainException>(Assert.Single(results, x => x is not null)).Code);
        await using var db = s.Database.CreateContext();
        var remaining = Assert.Single(await db.Administrators.ToArrayAsync());
        Assert.Contains(remaining.UserId, new[] { s.Seed.User.Id, s.Replacement.Id });
        Assert.Equal(1, await db.AuditEntries.CountAsync(x => x.Action == "AdministratorRemoved"));
        Assert.True(await db.Users.AnyAsync(x => x.Id == remaining.UserId && x.IsEligible && x.DepartureVerifiedUtc == null));
    }

    /// <summary>Ineligible assignments do not satisfy the last-administrator invariant and stale assignment versions cannot remove current roles.</summary>
    [Fact]
    public async Task LastEligibleAndStaleAssignmentGuardsPreserveRows()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await FoundationSeed.PersistAsync(s.Database, new Administrator { UserId = s.Seed.Other.Id });
        var actor = (await s.Service.ListAsync()).Single(x => x.UserId == s.Seed.User.Id);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.RemoveAsync(actor.UserId, actor.Version))).Code);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.RemoveAsync(actor.UserId, new byte[] { 1, 2, 3 }))).Code);
        await using var db = s.Database.CreateContext();
        Assert.Equal(2, await db.Administrators.CountAsync());
        Assert.Empty(await db.AuditEntries.ToArrayAsync());
    }

    /// <summary>SQL paging uses deterministic display-name/ID order and cannot request an oversized administrative roster.</summary>
    [Fact]
    public async Task AdministratorListingPagesBeforeReturningAssignments()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await s.Service.AddAsync(s.Replacement.Id, s.Replacement.Version);
        var all = await s.Service.ListAsync();
        Assert.Equal(2, all.Count);
        var first = Assert.Single(await s.Service.ListAsync(page: new(1, 1)));
        var second = Assert.Single(await s.Service.ListAsync(page: new(2, 1)));
        Assert.Equal(all[0].UserId, first.UserId);
        Assert.Equal(all[1].UserId, second.UserId);
        Assert.NotEqual(first.UserId, second.UserId);
        Assert.Empty(await s.Service.ListAsync(page: new(3, 1)));
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ListAsync(page: new(1, 101)))).Code);
    }
}
