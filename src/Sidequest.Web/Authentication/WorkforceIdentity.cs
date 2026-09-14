using System.Security.Claims;
using Sidequest.Application.Abstractions;

namespace Sidequest.Web.Authentication;

public static class WorkforceIdentity
{
    public static UserIdentity? Read(ClaimsPrincipal principal, FoundationAuthenticationSettings settings)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(principal.FindFirstValue("tid"), out var tenant) || tenant != settings.TenantId ||
            !Guid.TryParse(principal.FindFirstValue("oid"), out var objectId) || objectId == Guid.Empty ||
            !principal.HasClaim("roles", settings.WorkforceRole))
            return null;
        var synthetic = principal.HasClaim(FoundationAuthenticationSettings.SyntheticClaim, "true");
        if (settings.IsDevelopment != synthetic ||
            (settings.IsDevelopment && !DevelopmentPersonas.All.Any(p => p.ObjectId == objectId)))
            return null;
        return new(tenant, objectId, Limit(principal.FindFirstValue("name") ?? "Workforce user", 200),
            Limit(principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("email") ?? "", 320));
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
