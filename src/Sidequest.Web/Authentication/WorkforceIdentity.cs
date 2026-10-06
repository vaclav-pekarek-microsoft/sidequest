using System.Security.Claims;
using Sidequest.Application.Abstractions;

namespace Sidequest.Web.Authentication;

/// <summary>Maps application-issued authentication claims to stable tenant/object identities.</summary>
/// <remarks>There is no shared mutable state. Principals must not be mutated concurrently with claim inspection.</remarks>
public static class WorkforceIdentity
{
    /// <summary>Enforces account namespace, object, application admission role, and exact mode marker.</summary>
    /// <param name="principal">The authenticated principal issued by synthetic development or verified magic-code authentication.</param>
    /// <param name="settings">The allowed account namespace, admission role, and authentication mode.</param>
    /// <returns>An identity with bounded display/contact fields, or <see langword="null"/> when any admission requirement fails.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> or <paramref name="settings"/> is null.</exception>
    /// <remarks>This mapping does not check session expiry or persisted eligibility; callers must perform those checks separately.</remarks>
    /// <example>
    /// <code>
    /// var identity = WorkforceIdentity.Read(principal, settings);
    /// if (identity is null)
    /// {
    ///     throw new DomainException(ErrorCode.Forbidden, "Workforce admission failed.");
    /// }
    /// </code>
    /// </example>
    public static UserIdentity? Read(ClaimsPrincipal principal, FoundationAuthenticationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(settings);
        if (principal.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(principal.FindFirstValue("tid"), out var tenant) || tenant != settings.TenantId ||
            !Guid.TryParse(principal.FindFirstValue("oid"), out var objectId) || objectId == Guid.Empty ||
            !principal.HasClaim("roles", settings.WorkforceRole))
            return null;
        var synthetic = principal.HasClaim(FoundationAuthenticationSettings.SyntheticClaim, "true");
        var magicCode = principal.HasClaim(FoundationAuthenticationSettings.MagicCodeClaim, "true");
        if (settings.IsDevelopment != synthetic ||
            settings.IsMagicCode != magicCode ||
            synthetic == magicCode ||
            (settings.IsDevelopment && !DevelopmentPersonas.All.Any(p => p.ObjectId == objectId)))
            return null;
        var email = Limit(principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("email") ?? "", 320);
        if (settings.IsMagicCode)
        {
            try
            {
                if (!string.Equals(MagicAlias.Normalize(MagicAlias.LocalPart(email)), email, StringComparison.Ordinal))
                    return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        return new(tenant, objectId, Limit(principal.FindFirstValue("name") ?? "Workforce user", 200), email);
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
