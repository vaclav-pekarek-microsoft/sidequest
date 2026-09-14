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

public sealed class AuthenticationTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid ObjectId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T12:00:00Z");
    private static FoundationAuthenticationSettings Entra => new(false, Tenant, "Workforce", ObjectId);

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

    [Fact]
    public void MissingModeDefaultsToEntraEvenInDevelopment()
    {
        var settings = Load(ValidConfiguration(), "Development");
        Assert.False(settings.IsDevelopment);
        Assert.Equal(Tenant, settings.TenantId);
    }

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

    [Fact]
    public void TenantAndEmailAloneNeverAdmitGuestOrAnonymousPrincipal()
    {
        var principal = Principal();
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst("roles")!);
        Assert.Null(WorkforceIdentity.Read(principal, Entra));
        Assert.Null(WorkforceIdentity.Read(new ClaimsPrincipal(new ClaimsIdentity(Principal().Claims)), Entra));
    }

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

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Sidequest.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedAuthenticationState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
