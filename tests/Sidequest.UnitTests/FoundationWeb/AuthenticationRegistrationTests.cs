using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Sidequest.Web.Authentication;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Exercises the actual Microsoft.Identity.Web token-handler composition rather than hand-constructed admission claims.</summary>
public sealed class AuthenticationRegistrationTests
{
    /// <summary>Validated ID tokens retain the raw tenant, object and app-role names required by application admission.</summary>
    /// <param name="role">The signed role claim, which must exactly match the configured admission role.</param>
    /// <param name="admitted">Whether preserved claims satisfy application admission.</param>
    /// <returns>Completion after signature validation and exact admitted-identity assertions.</returns>
    [Theory]
    [InlineData("ApprovedParticipant", true)]
    [InlineData("OtherParticipant", false)]
    public async Task ConfiguredTokenHandlerPreservesApplicationAdmissionClaims(string role, bool admitted)
    {
        var tenant = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var objectId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        const string clientId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Staging" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = tenant.ToString(),
            ["AzureAd:ClientId"] = clientId,
            ["AzureAd:ClientSecret"] = "synthetic-unused-test-credential"
        });
        var settings = new FoundationAuthenticationSettings(false, tenant, "ApprovedParticipant", null);
        builder.Services.AddFoundationAuthentication(settings, builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.False(options.MapInboundClaims);

        var key = new SymmetricSecurityKey(Enumerable.Range(1, 64).Select(value => (byte)value).ToArray());
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://issuer.example.invalid",
            Audience = clientId,
            Subject = new ClaimsIdentity(
            [
                new("tid", tenant.ToString()), new("oid", objectId.ToString()),
                new("roles", role), new("name", "Approved participant")
            ]),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
        var validation = await options.TokenHandler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "https://issuer.example.invalid",
            ValidAudience = clientId,
            IssuerSigningKey = key,
            ValidateLifetime = false,
            NameClaimType = options.TokenValidationParameters.NameClaimType,
            RoleClaimType = options.TokenValidationParameters.RoleClaimType
        });
        Assert.True(validation.IsValid);
        var principal = new ClaimsPrincipal(validation.ClaimsIdentity);
        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.True(principal.HasClaim("tid", tenant.ToString()));
        Assert.True(principal.HasClaim("oid", objectId.ToString()));
        Assert.True(principal.HasClaim("roles", role));
        var identity = WorkforceIdentity.Read(principal, settings);
        if (admitted)
        {
            Assert.NotNull(identity);
            Assert.Equal(tenant, identity.TenantId);
            Assert.Equal(objectId, identity.ObjectId);
        }
        else
            Assert.Null(identity);
    }
}
