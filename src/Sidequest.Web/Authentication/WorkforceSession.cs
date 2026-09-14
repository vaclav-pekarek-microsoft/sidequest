using System.Globalization;
using System.Security.Claims;

namespace Sidequest.Web.Authentication;

/// <summary>Stores and validates an absolute session deadline carried by the protected application cookie.</summary>
/// <remarks>Stamping mutates the principal and must not run concurrently with reads or other mutations of that principal.</remarks>
public static class WorkforceSession
{
    /// <summary>The claim type holding the session deadline as invariant Unix time in seconds.</summary>
    public const string ExpiresClaim = "sidequest:session-expires";

    /// <summary>Replaces deadline claims on the primary claims identity with a one-hour deadline.</summary>
    /// <param name="principal">The newly admitted principal whose primary identity is a <see cref="ClaimsIdentity"/>.</param>
    /// <param name="now">The UTC issuance instant used to calculate expiry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is null.</exception>
    /// <exception cref="ArgumentException">The principal has no primary claims identity to stamp.</exception>
    /// <remarks>Call only when issuing a new session, not when reconnecting or periodically validating a circuit.</remarks>
    /// <example>
    /// <code>
    /// await accounts.ProvisionAsync(principal, cancellationToken);
    /// WorkforceSession.Stamp(principal, clock.GetUtcNow());
    /// await httpContext.SignInAsync(FoundationAuthenticationSettings.CookieScheme, principal);
    /// </code>
    /// </example>
    public static void Stamp(ClaimsPrincipal principal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not ClaimsIdentity identity)
        {
            throw new ArgumentException("A primary claims identity is required to stamp a session.", nameof(principal));
        }
        foreach (var claim in identity.FindAll(ExpiresClaim).ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new(ExpiresClaim, now.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Checks the protected session deadline without extending it.</summary>
    /// <param name="principal">The session principal bearing the deadline claim.</param>
    /// <param name="now">The current UTC instant, compared at Unix-second precision.</param>
    /// <returns><see langword="true"/> only before a valid deadline; missing, malformed, or expired claims return <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is null.</exception>
    public static bool IsCurrent(ClaimsPrincipal principal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return long.TryParse(principal.FindFirstValue(ExpiresClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var expires) &&
            now.ToUnixTimeSeconds() < expires;
    }
}
