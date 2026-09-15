using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Sidequest.Web.Authentication;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Verifies sign-in uniqueness, deadline enforcement and malformed server-principal rejection independently of browser hints.</summary>
public sealed class ExperienceSessionBindingTests
{
    /// <summary>Two sign-ins in the same second keep the same deadline but cannot reuse a prior circuit proof, even for the same workforce identity.</summary>
    [Fact]
    public void SameAccountSignInsAtSameInstantHaveDifferentBindings()
    {
        var clock = new BindingClock(DateTimeOffset.UtcNow);
        var binding = Binding(clock);
        var principal = Alice(clock.GetUtcNow());
        var proof = binding.Create(principal);
        var previousId = principal.FindFirstValue(WorkforceSession.IdClaim);
        var deadline = principal.FindFirstValue(WorkforceSession.ExpiresClaim);
        Assert.NotNull(proof);
        Assert.True(binding.Matches(proof, principal));
        WorkforceSession.Stamp(principal, clock.GetUtcNow());
        Assert.Equal(deadline, principal.FindFirstValue(WorkforceSession.ExpiresClaim));
        Assert.NotEqual(previousId, principal.FindFirstValue(WorkforceSession.IdClaim));
        Assert.Single(principal.FindAll(WorkforceSession.IdClaim));
        Assert.False(binding.Matches(proof, principal));
        Assert.True(binding.Matches(binding.Create(principal), principal));
    }

    /// <summary>Expired, anonymous, legacy and ambiguous principals cannot issue or match a circuit proof.</summary>
    /// <param name="invalid">The invalid server-principal partition.</param>
    [Theory]
    [InlineData("expired")]
    [InlineData("anonymous")]
    [InlineData("missing-session")]
    [InlineData("duplicate-session")]
    [InlineData("invalid-session")]
    [InlineData("wrong-tenant")]
    public void InvalidOrExpiredPrincipalCannotBind(string invalid)
    {
        var clock = new BindingClock(DateTimeOffset.UtcNow);
        var binding = Binding(clock);
        var principal = Alice(clock.GetUtcNow());
        var proof = binding.Create(principal);
        Assert.True(binding.Matches(proof, principal));
        var identity = (ClaimsIdentity)principal.Identity!;
        switch (invalid)
        {
            case "expired":
                var expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(principal.FindFirstValue(WorkforceSession.ExpiresClaim)!,
                    System.Globalization.CultureInfo.InvariantCulture));
                clock.Now = expiry.AddMilliseconds(-1);
                Assert.True(binding.Matches(proof, principal));
                clock.Now = expiry;
                break;
            case "anonymous":
                principal = new ClaimsPrincipal(new ClaimsIdentity(principal.Claims));
                break;
            case "missing-session":
                identity.RemoveClaim(identity.FindFirst(WorkforceSession.IdClaim)!);
                break;
            case "duplicate-session":
                identity.AddClaim(new Claim(WorkforceSession.IdClaim, Guid.NewGuid().ToString("D")));
                break;
            case "invalid-session":
                identity.RemoveClaim(identity.FindFirst(WorkforceSession.IdClaim)!);
                identity.AddClaim(new Claim(WorkforceSession.IdClaim, "not-a-session"));
                break;
            case "wrong-tenant":
                identity.RemoveClaim(identity.FindFirst("tid")!);
                identity.AddClaim(new Claim("tid", Guid.NewGuid().ToString("D")));
                break;
        }
        Assert.Null(binding.Create(principal));
        Assert.False(binding.Matches(proof, principal));
    }

    private static ExperienceSessionBinding Binding(TimeProvider clock) => new(new EphemeralDataProtectionProvider(),
        new FoundationAuthenticationSettings(true, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, null), clock);

    private static ClaimsPrincipal Alice(DateTimeOffset now)
    {
        var principal = DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All.Single(p => p.Name == "Alice"));
        WorkforceSession.Stamp(principal, now);
        return principal;
    }

    private sealed class BindingClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
