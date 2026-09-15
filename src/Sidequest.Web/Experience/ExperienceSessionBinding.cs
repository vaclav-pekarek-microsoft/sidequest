using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Sidequest.Web.Authentication;

namespace Sidequest.Web.Experience;

/// <summary>Protects a circuit's admitted identity and sign-in instance for comparison with independently authenticated HTTP cookies.</summary>
/// <param name="protection">The host's existing Data Protection provider; a feature-specific purpose prevents cross-protocol reuse.</param>
/// <param name="settings">Validated workforce admission settings for this host.</param>
/// <param name="clock">The same clock used to enforce the absolute authentication-session deadline.</param>
/// <remarks>The proof is a transient consistency check, not authentication or a resource-access grant. Keep it in bridge memory only,
/// never URLs, logs, IndexedDB, localStorage or offline snapshots. Cookie validation and resource authorization remain mandatory.</remarks>
public sealed class ExperienceSessionBinding(IDataProtectionProvider protection,
    FoundationAuthenticationSettings settings, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Sidequest.Experience.CircuitSession.v1");

    /// <summary>The request header carrying the in-memory circuit proof to the current-cookie comparison endpoint.</summary>
    public const string HeaderName = "X-Sidequest-Circuit-Binding";

    /// <summary>Captures a proof from the server-owned circuit principal, never a client-supplied identity.</summary>
    /// <param name="principal">The current circuit AuthenticationStateProvider's principal.</param>
    /// <returns>A protected proof, or null for anonymous, expired, malformed or pre-session-identifier authentication.</returns>
    public string? Create(ClaimsPrincipal principal)
    {
        var payload = Payload(principal);
        return payload is null ? null : protector.Protect(payload);
    }

    /// <summary>Compares a protected circuit proof with an independently authenticated HTTP principal and its exact sign-in instance.</summary>
    /// <param name="proof">Untrusted, bounded header data; absent, invalid and tampered values fail closed.</param>
    /// <param name="principal">The current HTTP cookie principal, already subject to host eligibility validation.</param>
    /// <returns>True only for the same admitted tenant/object identity, unique sign-in identifier and unexpired deadline.</returns>
    /// <remarks>No principal is constructed from the proof and no authentication or resource authorization is granted by it.</remarks>
    public bool Matches(string? proof, ClaimsPrincipal principal)
    {
        if (string.IsNullOrEmpty(proof) || proof.Length > 2048) return false;
        var payload = Payload(principal);
        if (payload is null) return false;
        try { return string.Equals(protector.Unprotect(proof), payload, StringComparison.Ordinal); }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    private string? Payload(ClaimsPrincipal principal)
    {
        var identity = WorkforceIdentity.Read(principal, settings);
        var sessions = principal.FindAll(WorkforceSession.IdClaim).Take(2).ToArray();
        var deadlines = principal.FindAll(WorkforceSession.ExpiresClaim).Take(2).ToArray();
        if (identity is null || !WorkforceSession.IsCurrent(principal, clock.GetUtcNow()) ||
            sessions.Length != 1 || deadlines.Length != 1 ||
            !Guid.TryParseExact(sessions[0].Value, "D", out var session) || session == Guid.Empty)
            return null;
        return $"{identity.TenantId:D}|{identity.ObjectId:D}|{session:D}|{deadlines[0].Value}";
    }
}
