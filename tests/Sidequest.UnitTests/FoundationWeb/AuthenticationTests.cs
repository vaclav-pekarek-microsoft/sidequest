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

/// <summary>Verifies startup admission guards, identity boundaries, local redirects, and absolute session expiry.</summary>
/// <remarks>Each test creates its own principals and mutable configuration; shared fields contain only immutable test values.</remarks>
public sealed class AuthenticationTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid ObjectId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T12:00:00Z");
    private static FoundationAuthenticationSettings Entra => new(false, Tenant, "Workforce", ObjectId);

    /// <summary>Verifies that explicitly selecting synthetic authentication cannot override a non-development host.</summary>
    /// <param name="environment">The non-development environment to reject.</param>
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
        Assert.Equal(DevelopmentPersonas.TenantId, settings.TenantId);
        Assert.Equal(DevelopmentPersonas.All.Single(p => p.Name == "Admin").ObjectId, settings.BootstrapAdministratorObjectId);
        Assert.Equal(4, DevelopmentPersonas.All.Select(p => p.ObjectId).Distinct().Count());
        Assert.All(DevelopmentPersonas.All, p => Assert.EndsWith("@sample.invalid", p.Email));
        configuration["Authentication:BootstrapAdministrator:ObjectId"] = DevelopmentPersonas.All[1].ObjectId.ToString();
        Assert.Throws<InvalidOperationException>(() => Load(configuration, "Development"));
        Assert.Null(Load(new() { ["Authentication:Mode"] = "Development" }, "Development").BootstrapAdministratorObjectId);
    }

    /// <summary>Verifies that omitting the mode never implicitly enables synthetic authentication.</summary>
    [Fact]
    public void MissingModeDefaultsToEntraEvenInDevelopment()
    {
        var settings = Load(ValidConfiguration(), "Development");
        Assert.False(settings.IsDevelopment);
        Assert.Equal(Tenant, settings.TenantId);
    }

    /// <summary>Verifies that invalid startup settings identify the offending configuration key.</summary>
    /// <param name="key">The setting to replace in otherwise valid configuration.</param>
    /// <param name="value">The missing or invalid value expected to fail startup validation.</param>
    [Theory]
    [InlineData("Authentication:Mode", "Fake")]
    [InlineData("AzureAd:TenantId", null)]
    [InlineData("AzureAd:TenantId", "common")]
    [InlineData("AzureAd:TenantId", "organizations")]
    [InlineData("AzureAd:ClientId", "")]
    [InlineData("AzureAd:ClientId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("AzureAd:ClientSecret", "")]
    [InlineData("AzureAd:Instance", "https://untrusted.invalid/")]
    [InlineData("Authentication:WorkforceRole", " ")]
    public void InvalidEntraConfigurationFailsVisibly(string key, string? value)
    {
        var configuration = ValidConfiguration();
        configuration[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => Load(configuration));
        Assert.Contains(key, error.Message);
    }

    /// <summary>Verifies that administrator bootstrap requires a complete tenant/object pair matching the admitted tenant.</summary>
    [Fact]
    public void BootstrapRequiresMatchingExplicitTenantAndObject()
    {
        var configuration = ValidConfiguration();
        configuration["Authentication:BootstrapAdministrator:ObjectId"] = ObjectId.ToString();
        Assert.Throws<InvalidOperationException>(() => Load(configuration));
        configuration["Authentication:BootstrapAdministrator:TenantId"] = Guid.NewGuid().ToString();
        Assert.Throws<InvalidOperationException>(() => Load(configuration));
        configuration["Authentication:BootstrapAdministrator:TenantId"] = Tenant.ToString();
        Assert.Equal(ObjectId, Load(configuration).BootstrapAdministratorObjectId);
        configuration.Remove("Authentication:BootstrapAdministrator:ObjectId");
        Assert.Throws<InvalidOperationException>(() => Load(configuration));
        Assert.Null(Load(ValidConfiguration()).BootstrapAdministratorObjectId);
    }

    /// <summary>Verifies tenant/object identity mapping and admission-role isolation from administrator permissions.</summary>
    [Fact]
    public void WorkforceAdmissionUsesTenantObjectAndRoleNotEmail()
    {
        var principal = Principal();
        var identity = Assert.IsType<UserIdentity>(WorkforceIdentity.Read(principal, Entra));
        Assert.Equal(Tenant, identity.TenantId);
        Assert.Equal(ObjectId, identity.ObjectId);
        Assert.Equal("unrelated@sample.invalid", identity.Email);
        Assert.Equal("Test workforce user", identity.DisplayName);
        Assert.False(principal.IsInRole("Administrator"));
    }

    /// <summary>Verifies rejection when one mandatory identity or admission claim is invalid.</summary>
    /// <param name="type">The claim type to replace on an otherwise valid principal.</param>
    /// <param name="value">The replacement value that must not satisfy admission.</param>
    [Theory]
    [InlineData("tid", "cccccccc-cccc-4ccc-8ccc-cccccccccccc")]
    [InlineData("oid", "00000000-0000-0000-0000-000000000000")]
    [InlineData("oid", "not-an-object-id")]
    [InlineData("roles", "Guest")]
    [InlineData("roles", "Administrator")]
    [InlineData("roles", "workforce")]
    public void WrongTenantInvalidObjectOrMissingWorkforceAssignmentIsRejected(string type, string value)
    {
        var principal = Principal();
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst(type)!);
        identity.AddClaim(new(type, value));
        Assert.Null(WorkforceIdentity.Read(principal, Entra));
    }

    /// <summary>Verifies that tenant/contact claims cannot substitute for workforce assignment or authentication.</summary>
    [Fact]
    public void TenantAndEmailAloneNeverAdmitGuestOrAnonymousPrincipal()
    {
        var principal = Principal();
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst("roles")!);
        Assert.Null(WorkforceIdentity.Read(principal, Entra));
        Assert.Null(WorkforceIdentity.Read(new ClaimsPrincipal(new ClaimsIdentity(Principal().Claims)), Entra));
    }

    /// <summary>Verifies mode isolation, the required synthetic marker, and rejection of unlisted synthetic objects.</summary>
    [Fact]
    public void SyntheticAndEntraPrincipalsCannotCrossModes()
    {
        var development = Load(new() { ["Authentication:Mode"] = "Development" }, "Development");
        var synthetic = DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]);
        Assert.Equal(DevelopmentPersonas.All[1].ObjectId, WorkforceIdentity.Read(synthetic, development)!.ObjectId);
        Assert.Null(WorkforceIdentity.Read(synthetic, development with { IsDevelopment = false }));
        ((ClaimsIdentity)synthetic.Identity!).RemoveClaim(synthetic.FindFirst(FoundationAuthenticationSettings.SyntheticClaim)!);
        Assert.Null(WorkforceIdentity.Read(synthetic, development));
        var unlisted = DevelopmentPersonas.CreatePrincipal(new DevelopmentPersona("Unknown", Guid.NewGuid()));
        Assert.Null(WorkforceIdentity.Read(unlisted, development));
    }

    /// <summary>Verifies that safe local paths survive normalization while external or malformed targets resolve to home.</summary>
    /// <param name="input">The untrusted candidate redirect destination.</param>
    /// <param name="expected">The accepted local path or root fallback.</param>
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

    /// <summary>Verifies that synthetic endpoints recognize only known direct loopback peers.</summary>
    /// <param name="address">The IPv4/IPv6 peer address, or an unknown address.</param>
    /// <param name="expected">Whether the request is allowed by the loopback guard.</param>
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

    /// <summary>Verifies that disabled or departed accounts are rejected before any contact or timestamp changes.</summary>
    /// <param name="eligible">The persisted account eligibility flag before sign-in.</param>
    /// <param name="departed">Whether persisted departure verification is present.</param>
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
            new(Tenant, ObjectId, "Changed", "changed@sample.invalid"), Now));
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Equal(eligible, user.IsEligible);
        Assert.Equal(departed ? Now : null, user.DepartureVerifiedUtc);
        Assert.Equal("Original", user.DisplayName);
        Assert.Null(user.LastSignedInUtc);
    }

    /// <summary>Verifies that eligible sign-in updates contact and UTC timestamp without changing tenant/object identity.</summary>
    [Fact]
    public void EligibleSignInUpdatesContactWithoutChangingIdentity()
    {
        var user = new UserAccount { TenantId = Tenant, ObjectId = ObjectId };
        WorkforceAccounts.UpdateContact(user, new(Tenant, ObjectId, "Updated", "new@sample.invalid"), Now);
        Assert.Equal("Updated", user.DisplayName);
        Assert.Equal("new@sample.invalid", user.Email);
        Assert.Equal(Now, user.LastSignedInUtc);
        Assert.True(user.IsEligible);
        Assert.Equal(Tenant, user.TenantId);
        Assert.Equal(ObjectId, user.ObjectId);
    }

    /// <summary>Verifies rejection of missing expiry and the exact non-sliding one-hour deadline boundary.</summary>
    [Fact]
    public void SessionExpiresAtOneHourAndDoesNotExtendOnCircuitReconnect()
    {
        var principal = Principal();
        Assert.False(WorkforceSession.IsCurrent(principal, Now));
        WorkforceSession.Stamp(principal, Now);
        Assert.True(WorkforceSession.IsCurrent(principal, Now.AddHours(1).AddSeconds(-1)));
        Assert.False(WorkforceSession.IsCurrent(principal, Now.AddHours(1)));
        Assert.False(WorkforceSession.IsCurrent(principal, Now.AddHours(1).AddSeconds(1)));
    }

    /// <summary>Verifies that session stamping fails with a specific parameter error when the principal has no claims identity.</summary>
    [Fact]
    public void SessionStampRequiresPrimaryClaimsIdentity()
    {
        var error = Assert.Throws<ArgumentException>(() => WorkforceSession.Stamp(new ClaimsPrincipal(), Now));
        Assert.Equal("principal", error.ParamName);
    }

    /// <summary>Verifies fail-fast argument errors for null inputs to identity, contact, configuration, and session boundaries.</summary>
    [Fact]
    public void AuthenticationBoundariesRejectNullArguments()
    {
        Assert.Equal("principal", Assert.Throws<ArgumentNullException>(() => WorkforceSession.Stamp(null!, Now)).ParamName);
        Assert.Equal("principal", Assert.Throws<ArgumentNullException>(() => WorkforceSession.IsCurrent(null!, Now)).ParamName);
        Assert.Equal("principal", Assert.Throws<ArgumentNullException>(() => WorkforceIdentity.Read(null!, Entra)).ParamName);
        Assert.Equal("settings", Assert.Throws<ArgumentNullException>(() => WorkforceIdentity.Read(Principal(), null!)).ParamName);
        Assert.Equal("user", Assert.Throws<ArgumentNullException>(() =>
            WorkforceAccounts.UpdateContact(null!, new(Tenant, ObjectId, "Test", ""), Now)).ParamName);
        Assert.Equal("identity", Assert.Throws<ArgumentNullException>(() =>
            WorkforceAccounts.UpdateContact(new UserAccount(), null!, Now)).ParamName);
        Assert.Equal("configuration", Assert.Throws<ArgumentNullException>(() =>
            FoundationAuthenticationSettings.Load(null!, new TestEnvironment())).ParamName);
        Assert.Equal("environment", Assert.Throws<ArgumentNullException>(() =>
            FoundationAuthenticationSettings.Load(new ConfigurationBuilder().Build(), null!)).ParamName);
    }

    /// <summary>Verifies circuit-state identity lookup, expiry rejection, and cancellation before state access.</summary>
    /// <returns>A task completing after the identity and cancellation assertions.</returns>
    [Fact]
    public async Task CurrentUserReadsCircuitStateAndRejectsExpiredSession()
    {
        var principal = Principal();
        WorkforceSession.Stamp(principal, Now);
        var current = new CircuitCurrentUser(new FixedAuthenticationState(principal), Entra, new FixedClock(Now));
        Assert.Equal(ObjectId, (await current.GetIdentityAsync())!.ObjectId);
        current = new(new FixedAuthenticationState(principal), Entra, new FixedClock(Now.AddHours(1)));
        Assert.Null(await current.GetIdentityAsync());
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await current.GetIdentityAsync(new CancellationToken(true)));
    }

    /// <summary>Verifies the explicit HTTP outcome for each application-domain failure category.</summary>
    /// <param name="code">The domain classification to map.</param>
    /// <param name="expected">The expected HTTP status code.</param>
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
        ["AzureAd:TenantId"] = Tenant.ToString(),
        ["AzureAd:ClientId"] = "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
        ["AzureAd:ClientSecret"] = "test-only-not-a-real-secret",
        ["Authentication:WorkforceRole"] = "Workforce"
    };

    private static FoundationAuthenticationSettings Load(Dictionary<string, string?> configuration, string environment = "Production") =>
        FoundationAuthenticationSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
            new TestEnvironment { EnvironmentName = environment });

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new("tid", Tenant.ToString()), new("oid", ObjectId.ToString()),
        new("roles", "Workforce"), new("name", "Test workforce user"),
        new("preferred_username", "unrelated@sample.invalid")
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
    /// <param name="now">The UTC instant returned by every clock read.</param>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Returns the fixed UTC instant supplied by the test.</summary>
        /// <returns>The configured instant without advancing with wall-clock time.</returns>
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Supplies a principal directly to the circuit current-user adapter without an HTTP context.</summary>
    /// <param name="principal">The authenticated or expired-session principal under test.</param>
    private sealed class FixedAuthenticationState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        /// <summary>Returns authentication state for the fixed principal supplied by the test.</summary>
        /// <returns>A completed task carrying the test principal.</returns>
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
