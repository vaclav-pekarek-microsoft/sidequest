using System.Security.Claims;

namespace Sidequest.Web.Authentication;

public sealed record DevelopmentPersona(string Name, Guid ObjectId)
{
    public string Email => $"{Name.ToLowerInvariant()}@sample.invalid";
}

public static class DevelopmentPersonas
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public const string WorkforceRole = "Sidequest.SyntheticWorkforce";
    public static IReadOnlyList<DevelopmentPersona> All { get; } = Array.AsReadOnly(new[]
    {
        new DevelopmentPersona("Admin", Guid.Parse("22222222-2222-4222-8222-222222222221")),
        new DevelopmentPersona("Alice", Guid.Parse("22222222-2222-4222-8222-222222222222")),
        new DevelopmentPersona("Bob", Guid.Parse("22222222-2222-4222-8222-222222222223")),
        new DevelopmentPersona("Carol", Guid.Parse("22222222-2222-4222-8222-222222222224"))
    });

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
