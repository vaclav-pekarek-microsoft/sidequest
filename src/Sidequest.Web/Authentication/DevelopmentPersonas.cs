using System.Security.Claims;

namespace Sidequest.Web.Authentication;

/// <summary>Identifies a named synthetic account used exclusively by the development sign-in flow.</summary>
/// <param name="Name">The display name and local part used for the synthetic contact address.</param>
/// <param name="ObjectId">The fixed synthetic object identifier, not an identifier for a real workforce account.</param>
public sealed record DevelopmentPersona(string Name, Guid ObjectId)
{
    /// <summary>Gets the lowercase contact address in the reserved <c>sample.invalid</c> domain.</summary>
    public string Email => $"{Name.ToLowerInvariant()}@sample.invalid";
}

/// <summary>Defines the documented synthetic tenant and immutable selection of local test personas.</summary>
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
