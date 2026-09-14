using System.Security.Claims;

namespace Sidequest.Web.Authentication;

/// <summary>Defines the documented synthetic tenant and immutable selection of local test personas.</summary>
/// <remarks>The read-only catalog is safe to share. Each principal returned by this class belongs to its caller.</remarks>
public static class DevelopmentPersonas
{
    /// <summary>The fixed tenant identifier shared by all synthetic development personas.</summary>
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    /// <summary>The admission-only app-role value used in synthetic principals.</summary>
    public const string WorkforceRole = "Sidequest.SyntheticWorkforce";
    /// <summary>Gets Admin, Alice, Bob, and Carol in their documented display order.</summary>
    public static IReadOnlyList<DevelopmentPersona> All { get; } = Array.AsReadOnly(new[]
    {
        new DevelopmentPersona("Admin", Guid.Parse("22222222-2222-4222-8222-222222222221")),
        new DevelopmentPersona("Alice", Guid.Parse("22222222-2222-4222-8222-222222222222")),
        new DevelopmentPersona("Bob", Guid.Parse("22222222-2222-4222-8222-222222222223")),
        new DevelopmentPersona("Carol", Guid.Parse("22222222-2222-4222-8222-222222222224"))
    });

    /// <summary>Creates an authenticated synthetic principal without provisioning SQL or stamping session expiry.</summary>
    /// <param name="persona">The persona whose identifier and display/contact information populate claims.</param>
    /// <returns>A cookie-scheme principal bearing the synthetic marker and workforce admission role.</returns>
    /// <remarks>Callers must enforce Development mode and loopback access; workforce validation rejects unlisted object identifiers.</remarks>
    public static ClaimsPrincipal CreatePrincipal(DevelopmentPersona persona) => new(new ClaimsIdentity(
    [
        new("tid", TenantId.ToString()),
        new("oid", persona.ObjectId.ToString()),
        new("name", persona.Name),
        new("preferred_username", persona.Email),
        new("roles", WorkforceRole),
        new(FoundationAuthenticationSettings.SyntheticClaim, "true")
    ], FoundationAuthenticationSettings.CookieScheme, "name", "roles"));
}
