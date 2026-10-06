using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Authentication;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Verifies magic-code startup guards, identity boundaries, redirects, and absolute session expiry.</summary>
public sealed class AuthenticationTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid ObjectId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T12:00:00Z");
    private static FoundationAuthenticationSettings Magic => new(false, Tenant,
        FoundationAuthenticationSettings.MagicCodeRole, null);

    /// <summary>Verifies deployed mode uses the configured account namespace and normalized bootstrap alias.</summary>
    [Fact]
    public void MagicCodeModeLoadsAccountNamespaceAndBootstrapAlias()
    {
        var configuration = ValidConfiguration();
        configuration["Authentication:BootstrapAdministrator:Alias"] = "Test.Alias";
        var settings = Load(configuration);
        Assert.True(settings.IsMagicCode);
        Assert.False(settings.IsDevelopment);
        Assert.Equal(Tenant, settings.TenantId);
        Assert.Equal(FoundationAuthenticationSettings.MagicCodeRole, settings.WorkforceRole);
        Assert.Equal("test.alias@microsoft.com", settings.BootstrapAdministratorEmail);
        Assert.Null(settings.BootstrapAdministratorObjectId);
    }

    /// <summary>Verifies omitted mode safely defaults to deployed magic-code authentication.</summary>
    [Fact]
    public void MissingModeDefaultsToMagicCode()
    {
        var settings = Load(new() { ["Authentication:AccountNamespaceId"] = Tenant.ToString() }, "Development");
        Assert.True(settings.IsMagicCode);
        Assert.Equal(Tenant, settings.TenantId);
    }

    /// <summary>Verifies invalid deployed configuration fails visibly at startup.</summary>
    /// <param name="key">Setting to replace in otherwise valid configuration.</param>
    /// <param name="value">Invalid replacement.</param>
    [Theory]
    [InlineData("Authentication:Mode", "Entra")]
    [InlineData("Authentication:Mode", "Fake")]
    [InlineData("Authentication:AccountNamespaceId", null)]
    [InlineData("Authentication:AccountNamespaceId", "common")]
    [InlineData("Authentication:AccountNamespaceId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("Authentication:BootstrapAdministrator:Alias", "user@outside.invalid")]
    [InlineData("Authentication:BootstrapAdministrator:Alias", "user@microsoft.com")]
    public void InvalidMagicCodeConfigurationFailsVisibly(string key, string? value)
    {
        var configuration = ValidConfiguration();
        configuration[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => Load(configuration));
        Assert.Contains(key, error.Message);
    }

    /// <summary>Verifies legacy object-pair bootstrap settings cannot silently grant deployed magic-code administration.</summary>
    [Fact]
    public void MagicCodeBootstrapRejectsLegacyObjectPair()
    {
        var configuration = ValidConfiguration();
        configuration["Authentication:BootstrapAdministrator:TenantId"] = Tenant.ToString();
        configuration["Authentication:BootstrapAdministrator:ObjectId"] = ObjectId.ToString();
        Assert.Throws<InvalidOperationException>(() => Load(configuration));
    }

    /// <summary>Verifies explicitly selecting synthetic authentication cannot override a non-development host.</summary>
    /// <param name="environment">Non-development environment to reject.</param>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void SyntheticModeIsRejectedOutsideDevelopment(string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(
            new() { ["Authentication:Mode"] = "Development" }, environment));
        Assert.Contains("only in the Development", error.Message);
    }

    /// <summary>Verifies fixed synthetic identities and explicit bootstrap of only the documented Admin object.</summary>
    [Fact]
    public void ExplicitDevelopmentModeUsesOnlyDocumentedAdmin()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Development",
            ["Authentication:BootstrapAdministrator:TenantId"] = DevelopmentPersonas.TenantId.ToString(),
            ["Authentication:BootstrapAdministrator:ObjectId"] = DevelopmentPersonas.All[0].ObjectId.ToString()
        };
        var settings = Load(configuration, "Development");
        Assert.True(settings.IsDevelopment);
        Assert.False(settings.IsMagicCode);
        Assert.Equal(DevelopmentPersonas.All[0].ObjectId, settings.BootstrapAdministratorObjectId);
        configuration["Authentication:BootstrapAdministrator:ObjectId"] = DevelopmentPersonas.All[1].ObjectId.ToString();
        Assert.Throws<InvalidOperationException>(() => Load(configuration, "Development"));
    }

    /// <summary>Verifies application-issued magic claims map to the configured stable account key.</summary>
    [Fact]
    public void MagicCodeIdentityUsesTenantObjectAndMicrosoftMailbox()
    {
        var identity = Assert.IsType<UserIdentity>(WorkforceIdentity.Read(Principal(), Magic));
        Assert.Equal(Tenant, identity.TenantId);
        Assert.Equal(ObjectId, identity.ObjectId);
        Assert.Equal("test.alias@microsoft.com", identity.Email);
        Assert.Equal("Test workforce user", identity.DisplayName);
    }

    /// <summary>Verifies every required magic-code claim and namespace boundary fails closed.</summary>
    /// <param name="type">Claim type to replace.</param>
    /// <param name="value">Replacement that must not satisfy admission.</param>
    [Theory]
    [InlineData("tid", "cccccccc-cccc-4ccc-8ccc-cccccccccccc")]
    [InlineData("oid", "00000000-0000-0000-0000-000000000000")]
    [InlineData("oid", "not-an-object-id")]
    [InlineData("roles", "Guest")]
    [InlineData("preferred_username", "test.alias@outside.invalid")]
    [InlineData(FoundationAuthenticationSettings.MagicCodeClaim, "false")]
    public void InvalidMagicCodeIdentityClaimIsRejected(string type, string value)
    {
        var principal = Principal();
        var claims = (ClaimsIdentity)principal.Identity!;
        claims.RemoveClaim(claims.FindFirst(type)!);
        claims.AddClaim(new(type, value));
        Assert.Null(WorkforceIdentity.Read(principal, Magic));
    }

    /// <summary>Verifies synthetic and deployed principals cannot cross authentication modes.</summary>
    [Fact]
    public void SyntheticAndMagicCodePrincipalsCannotCrossModes()
    {
        var development = Load(new() { ["Authentication:Mode"] = "Development" }, "Development");
        var synthetic = DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]);
        Assert.NotNull(WorkforceIdentity.Read(synthetic, development));
        Assert.Null(WorkforceIdentity.Read(synthetic, Magic));
        Assert.Null(WorkforceIdentity.Read(Principal(), development));
    }

    /// <summary>Verifies aliases normalize to one exact Microsoft mailbox and reject domains supplied by the user.</summary>
    /// <param name="alias">Candidate alias.</param>
    /// <param name="expected">Normalized mailbox, or null when rejection is expected.</param>
    [Theory]
    [InlineData("Test.Alias", "test.alias@microsoft.com")]
    [InlineData(" user-name ", "user-name@microsoft.com")]
    [InlineData("", null)]
    [InlineData("user@microsoft.com", null)]
    [InlineData("user name", null)]
    [InlineData(".user", null)]
    [InlineData("user+", null)]
    public void MicrosoftAliasNormalizationIsExact(string alias, string? expected)
    {
        if (expected is null)
            Assert.Throws<ArgumentException>(() => MagicAlias.Normalize(alias));
        else
            Assert.Equal(expected, MagicAlias.Normalize(alias));
    }

    /// <summary>Verifies stored Microsoft mailboxes normalize before account-link comparisons.</summary>
    /// <param name="email">Stored mailbox candidate.</param>
    /// <param name="expected">Normalized mailbox, or null when the candidate is not a Microsoft alias mailbox.</param>
    [Theory]
    [InlineData(" Test.Alias@Microsoft.com ", "test.alias@microsoft.com")]
    [InlineData("test_alias@MICROSOFT.COM", "test_alias@microsoft.com")]
    [InlineData("test.alias@example.com", null)]
    [InlineData("test.alias@@microsoft.com", null)]
    [InlineData(".test@microsoft.com", null)]
    public void StoredMicrosoftMailboxNormalizationIsExact(string email, string? expected)
    {
        var result = MagicAlias.TryNormalizeMailbox(email, out var normalized);

        Assert.Equal(expected is not null, result);
        Assert.Equal(expected ?? "", normalized);
    }

    /// <summary>Verifies new account object identifiers are stable per namespace and mailbox.</summary>
    [Fact]
    public void MagicCodeObjectIdentityIsStableAndNamespaced()
    {
        var first = MagicCodeAuthenticationService.CreateObjectId(Tenant, "test.alias@microsoft.com");
        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, MagicCodeAuthenticationService.CreateObjectId(Tenant, "test.alias@microsoft.com"));
        Assert.NotEqual(first, MagicCodeAuthenticationService.CreateObjectId(Guid.NewGuid(), "test.alias@microsoft.com"));
        Assert.NotEqual(first, MagicCodeAuthenticationService.CreateObjectId(Tenant, "other@microsoft.com"));
    }

    /// <summary>Verifies safe local paths survive normalization while external or malformed targets resolve to home.</summary>
    /// <param name="input">Untrusted redirect destination.</param>
    /// <param name="expected">Accepted local path or root fallback.</param>
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://outside.invalid", "/")]
    [InlineData("//outside.invalid", "/")]
    [InlineData("/\\outside.invalid", "/")]
    [InlineData("/a\\b", "/")]
    [InlineData("/\r\nLocation:evil", "/")]
    [InlineData("/foundation", "/foundation")]
    [InlineData("/foundation?preview=true", "/foundation?preview=true")]
    public void ReturnUrlsAreLocalOnly(string? input, string expected) =>
        Assert.Equal(expected, AuthenticationEndpoints.LocalReturnUrl(input));

    /// <summary>Verifies synthetic endpoints recognize only known direct loopback peers.</summary>
    /// <param name="address">IPv4/IPv6 peer address, or unknown.</param>
    /// <param name="expected">Whether the request is allowed.</param>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.0.2.4", false)]
    [InlineData(null, false)]
    public void SyntheticEndpointsRequireLoopback(string? address, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        Assert.Equal(expected, AuthenticationEndpoints.IsLoopback(context));
    }

    /// <summary>Verifies disabled or departed accounts are rejected before contact or timestamp changes.</summary>
    /// <param name="eligible">Persisted eligibility flag.</param>
    /// <param name="departed">Whether departure verification is present.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void SignInNeverReenablesDisabledOrDepartedAccount(bool eligible, bool departed)
    {
        var user = new UserAccount
        {
            IsEligible = eligible, DepartureVerifiedUtc = departed ? Now : null, DisplayName = "Original"
        };
        var error = Assert.Throws<DomainException>(() => WorkforceAccounts.UpdateContact(user,
            new(Tenant, ObjectId, "Changed", "changed@microsoft.com"), Now));
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Equal("Original", user.DisplayName);
        Assert.Null(user.LastSignedInUtc);
    }

    /// <summary>Verifies eligible sign-in updates contact and UTC timestamp without changing stable identity.</summary>
    [Fact]
    public void EligibleSignInUpdatesContactWithoutChangingIdentity()
    {
        var user = new UserAccount { TenantId = Tenant, ObjectId = ObjectId };
        WorkforceAccounts.UpdateContact(user, new(Tenant, ObjectId, "Updated", "new@microsoft.com"), Now);
        Assert.Equal("Updated", user.DisplayName);
        Assert.Equal("new@microsoft.com", user.Email);
        Assert.Equal(Now, user.LastSignedInUtc);
        Assert.Equal(Tenant, user.TenantId);
        Assert.Equal(ObjectId, user.ObjectId);
    }

    /// <summary>Verifies missing expiry and the exact non-sliding one-hour deadline boundary.</summary>
    [Fact]
    public void SessionExpiresAtOneHourAndDoesNotSlide()
    {
        var principal = Principal();
        Assert.False(WorkforceSession.IsCurrent(principal, Now));
        WorkforceSession.Stamp(principal, Now);
        Assert.True(WorkforceSession.IsCurrent(principal, Now.AddHours(1).AddSeconds(-1)));
        Assert.False(WorkforceSession.IsCurrent(principal, Now.AddHours(1)));
    }

    /// <summary>Verifies fail-fast argument errors for null configuration and identity inputs.</summary>
    [Fact]
    public void AuthenticationBoundariesRejectNullArguments()
    {
        Assert.Equal("principal", Assert.Throws<ArgumentNullException>(() => WorkforceIdentity.Read(null!, Magic)).ParamName);
        Assert.Equal("settings", Assert.Throws<ArgumentNullException>(() => WorkforceIdentity.Read(Principal(), null!)).ParamName);
        Assert.Equal("configuration", Assert.Throws<ArgumentNullException>(() =>
            FoundationAuthenticationSettings.Load(null!, new TestEnvironment())).ParamName);
        Assert.Equal("environment", Assert.Throws<ArgumentNullException>(() =>
            FoundationAuthenticationSettings.Load(new ConfigurationBuilder().Build(), null!)).ParamName);
    }

    /// <summary>Verifies circuit-state identity lookup, expiry rejection, and cancellation before state access.</summary>
    /// <returns>A task completing after identity and cancellation assertions.</returns>
    [Fact]
    public async Task CurrentUserReadsCircuitStateAndRejectsExpiredSession()
    {
        var principal = Principal();
        WorkforceSession.Stamp(principal, Now);
        var current = new CircuitCurrentUser(new FixedAuthenticationState(principal), Magic, new FixedClock(Now));
        Assert.Equal(ObjectId, (await current.GetIdentityAsync())!.ObjectId);
        current = new(new FixedAuthenticationState(principal), Magic, new FixedClock(Now.AddHours(1)));
        Assert.Null(await current.GetIdentityAsync());
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await current.GetIdentityAsync(new CancellationToken(true)));
    }

    /// <summary>Verifies the explicit HTTP outcome for each application-domain failure category.</summary>
    /// <param name="code">Domain classification to map.</param>
    /// <param name="expected">Expected HTTP status code.</param>
    [Theory]
    [InlineData(ErrorCode.Validation, 400)]
    [InlineData(ErrorCode.Forbidden, 403)]
    [InlineData(ErrorCode.NotFound, 404)]
    [InlineData(ErrorCode.Conflict, 409)]
    [InlineData(ErrorCode.DependencyUnavailable, 503)]
    public void DomainErrorsHaveExplicitHttpOutcomes(ErrorCode code, int expected) =>
        Assert.Equal(expected, SafeExceptionHandler.Status(new DomainException(code, "not disclosed")));

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["Authentication:Mode"] = "MagicCode",
        ["Authentication:AccountNamespaceId"] = Tenant.ToString()
    };

    private static FoundationAuthenticationSettings Load(
        Dictionary<string, string?> configuration, string environment = "Production") =>
        FoundationAuthenticationSettings.Load(
            new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
            new TestEnvironment { EnvironmentName = environment });

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new("tid", Tenant.ToString()),
        new("oid", ObjectId.ToString()),
        new("roles", FoundationAuthenticationSettings.MagicCodeRole),
        new("name", "Test workforce user"),
        new("preferred_username", "test.alias@microsoft.com"),
        new(FoundationAuthenticationSettings.MagicCodeClaim, "true")
    ], "test", "name", "roles"));

    /// <summary>Provides a configurable host environment without accessing project or host files.</summary>
    private sealed class TestEnvironment : IHostEnvironment
    {
        /// <inheritdoc/>
        public string EnvironmentName { get; set; } = "Production";
        /// <inheritdoc/>
        public string ApplicationName { get; set; } = "Sidequest.Tests";
        /// <inheritdoc/>
        public string ContentRootPath { get; set; } = "";
        /// <inheritdoc/>
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Freezes UTC time for deterministic session-boundary assertions.</summary>
    /// <param name="now">UTC instant returned by every clock read.</param>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Supplies a principal directly to the circuit current-user adapter.</summary>
    /// <param name="principal">Authenticated or expired-session principal under test.</param>
    private sealed class FixedAuthenticationState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        /// <inheritdoc/>
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
