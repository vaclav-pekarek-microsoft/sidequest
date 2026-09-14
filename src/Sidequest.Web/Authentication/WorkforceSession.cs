using System.Globalization;
using System.Security.Claims;

namespace Sidequest.Web.Authentication;

public static class WorkforceSession
{
    public const string ExpiresClaim = "sidequest:session-expires";

    public static void Stamp(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var claim in identity.FindAll(ExpiresClaim).ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new(ExpiresClaim, now.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
    }

    public static bool IsCurrent(ClaimsPrincipal principal, DateTimeOffset now) =>
        long.TryParse(principal.FindFirstValue(ExpiresClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var expires) &&
        now.ToUnixTimeSeconds() < expires;
}
