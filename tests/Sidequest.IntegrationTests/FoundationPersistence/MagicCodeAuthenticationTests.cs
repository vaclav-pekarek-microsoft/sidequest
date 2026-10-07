using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.Web.Authentication;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Verifies magic-code delivery, SQL consumption, account continuity, and failure limits against real SQL Server.</summary>
/// <param name="database">Uniquely owned migrated SQL fixture for this class.</param>
public sealed class MagicCodeAuthenticationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T12:00:00Z");

    /// <summary>A delivered code creates one account and administrator grant, authenticates once, and cannot be reused.</summary>
    /// <returns>A task completing after persisted challenge, account, principal, and reuse assertions.</returns>
    [Fact]
    public async Task DeliveredCodeCreatesAccountAndIsSingleUse()
    {
        var factory = new ProvisioningTestContext(database);
        var gateway = new RecordingEmailGateway();
        var settings = LoadSettings(factory.TenantId, "new.user");
        var service = CreateService(factory, gateway, settings);

        var challengeId = await service.RequestAsync("New.User", false, default);
        var message = Assert.Single(gateway.Messages);
        Assert.Equal("new.user@microsoft.com", message.Recipient);
        var code = AssertCode(message.TextBody);

        await using (var read = database.CreateContext())
        {
            var challenge = await read.MagicSignInChallenges.SingleAsync(x => x.Id == challengeId);
            Assert.Equal("new.user@microsoft.com", challenge.Email);
            Assert.Equal(16, challenge.CodeSalt.Length);
            Assert.Equal(32, challenge.CodeHash.Length);
            Assert.Null(challenge.ConsumedUtc);
        }

        var principal = await service.VerifyAsync(challengeId, code, default);
        Assert.NotNull(principal);
        Assert.Equal(factory.TenantId.ToString(), principal.FindFirst("tid")?.Value);
        Assert.Equal("new.user@microsoft.com", principal.FindFirst("preferred_username")?.Value);
        Assert.True(principal.HasClaim(FoundationAuthenticationSettings.MagicCodeClaim, "true"));
        Assert.Null(await service.VerifyAsync(challengeId, code, default));

        await using var final = database.CreateContext();
        var user = await final.Users.SingleAsync(x => x.Email == "new.user@microsoft.com");
        Assert.Equal(MagicCodeAuthenticationService.CreateObjectId(factory.TenantId, user.Email), user.ObjectId);
        Assert.Equal(Now, user.LastSignedInUtc);
        Assert.True(await final.Administrators.AnyAsync(x => x.UserId == user.Id));
        Assert.NotNull((await final.MagicSignInChallenges.SingleAsync(x => x.Id == challengeId)).ConsumedUtc);
    }

    /// <summary>A verified address links exactly one existing eligible account and five wrong attempts consume the challenge.</summary>
    /// <returns>A task completing after account continuity and attempt-exhaustion assertions.</returns>
    [Fact]
    public async Task ExistingAccountIsLinkedAndWrongAttemptsAreBounded()
    {
        var factory = new ProvisioningTestContext(database);
        var gateway = new RecordingEmailGateway();
        var settings = LoadSettings(factory.TenantId);
        var existing = new UserAccount
        {
            TenantId = factory.TenantId,
            ObjectId = Guid.NewGuid(),
            DisplayName = "Existing display name",
            Email = " Existing@Microsoft.com "
        };
        await FoundationSeed.PersistAsync(database, existing);
        var service = CreateService(factory, gateway, settings);

        var linkChallenge = await service.RequestAsync("existing", false, default);
        var principal = await service.VerifyAsync(
            linkChallenge, AssertCode(Assert.Single(gateway.Messages).TextBody), default);
        Assert.NotNull(principal);
        Assert.Equal(existing.ObjectId.ToString(), principal.FindFirst("oid")?.Value);
        Assert.Equal("Existing display name", principal.Identity?.Name);
        Assert.Equal("existing@microsoft.com", principal.FindFirst("preferred_username")?.Value);
        Assert.NotNull(WorkforceIdentity.Read(principal, settings));

        var exhaustedChallenge = await service.RequestAsync("other", false, default);
        var throttledDecoy = await service.RequestAsync("other", false, default);
        Assert.NotEqual(exhaustedChallenge, throttledDecoy);
        Assert.Equal(2, gateway.Messages.Count);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Null(await service.VerifyAsync(exhaustedChallenge, "999999", default));
        await using var read = database.CreateContext();
        var exhausted = await read.MagicSignInChallenges.SingleAsync(x => x.Id == exhaustedChallenge);
        Assert.Equal(5, exhausted.AttemptCount);
        Assert.NotNull(exhausted.ConsumedUtc);
        Assert.False(await read.Users.AnyAsync(x => x.Email == "other@microsoft.com"));
    }

    /// <summary>Duplicate stored addresses that normalize to one alias fail closed without creating another account.</summary>
    /// <returns>A task completing after the ambiguous normalized-alias match is rejected.</returns>
    [Fact]
    public async Task DuplicateNormalizedAliasesAreAmbiguous()
    {
        var factory = new ProvisioningTestContext(database);
        var gateway = new RecordingEmailGateway();
        var settings = LoadSettings(factory.TenantId);
        await FoundationSeed.PersistAsync(database,
            new UserAccount
            {
                TenantId = factory.TenantId,
                ObjectId = Guid.NewGuid(),
                DisplayName = "First duplicate",
                Email = "Duplicate@Microsoft.com"
            },
            new UserAccount
            {
                TenantId = factory.TenantId,
                ObjectId = Guid.NewGuid(),
                DisplayName = "Second duplicate",
                Email = " duplicate@microsoft.com "
            });
        var service = CreateService(factory, gateway, settings);

        var challengeId = await service.RequestAsync("DUPLICATE", false, default);
        var principal = await service.VerifyAsync(
            challengeId, AssertCode(Assert.Single(gateway.Messages).TextBody), default);

        Assert.Null(principal);
        await using var read = database.CreateContext();
        Assert.Equal(2, await read.Users.CountAsync(user =>
            user.TenantId == factory.TenantId && user.Email.ToLower().Contains("duplicate")));
        Assert.NotNull((await read.MagicSignInChallenges.SingleAsync(
            challenge => challenge.Id == challengeId)).ConsumedUtc);
    }

    /// <summary>A local development request hashes the fixed code, skips email delivery, and uses the normal single-use verification path.</summary>
    /// <returns>A task completing after local-code verification and consumption assertions.</returns>
    [Fact]
    public async Task LocalDevelopmentCodeUsesNormalChallengeVerification()
    {
        var factory = new ProvisioningTestContext(database);
        var gateway = new RecordingEmailGateway();
        var service = CreateService(factory, gateway, LoadSettings(factory.TenantId));

        var challengeId = await service.RequestAsync("local.user", true, default);

        Assert.Empty(gateway.Messages);
        Assert.Null(await service.VerifyAsync(challengeId, "123456", default));
        var principal = await service.VerifyAsync(challengeId, "000000", default);
        Assert.NotNull(principal);
        Assert.Equal("local.user@microsoft.com", principal.FindFirst("preferred_username")?.Value);
        Assert.Null(await service.VerifyAsync(challengeId, "000000", default));
    }

    private static MagicCodeAuthenticationService CreateService(
        ProvisioningTestContext factory,
        RecordingEmailGateway gateway,
        FoundationAuthenticationSettings settings) =>
        new(factory, gateway, settings, new FixedClock(Now),
            NullLogger<MagicCodeAuthenticationService>.Instance);

    private static string AssertCode(string text)
    {
        var match = Regex.Match(text, @"\b\d{6}\b", RegexOptions.CultureInvariant);
        Assert.True(match.Success);
        return match.Value;
    }

    private static FoundationAuthenticationSettings LoadSettings(Guid tenantId, string? bootstrapAlias = null) =>
        FoundationAuthenticationSettings.Load(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "MagicCode",
                ["Authentication:AccountNamespaceId"] = tenantId.ToString(),
                ["Authentication:BootstrapAdministrator:Alias"] = bootstrapAlias
            }).Build(),
            new TestEnvironment());

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        /// <inheritdoc/>
        public string EnvironmentName { get; set; } = Environments.Production;
        /// <inheritdoc/>
        public string ApplicationName { get; set; } = "Sidequest.IntegrationTests";
        /// <inheritdoc/>
        public string ContentRootPath { get; set; } = "";
        /// <inheritdoc/>
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
