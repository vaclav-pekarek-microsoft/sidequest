using Microsoft.AspNetCore.Authentication;

namespace Sidequest.Web.Authentication;

/// <summary>Binds successful HTTP sign-in completion to the device generation that initiated it, without storing tokens in browser data.</summary>
public static class ExperienceAuthentication
{
    /// <summary>The protected authentication-property key carrying the non-secret device generation from the sign-in attempt.</summary>
    public const string EpochProperty = "sidequest:experience-epoch";

    /// <summary>Creates properties preserved by cookie sign-in or protected OIDC state until successful completion.</summary>
    /// <param name="returnUrl">Untrusted requested destination, constrained to a local absolute path.</param>
    /// <param name="epoch">A device-generated UUID, or null if clearing was unavailable; it never grants authentication or authorization.</param>
    /// <returns>Non-persistent authentication properties whose completion redirect cannot leave this origin.</returns>
    /// <remarks>Invalid or absent device generations deliberately disable automatic cache activation; sign-in itself remains available.</remarks>
    public static AuthenticationProperties CreateProperties(string? returnUrl, string? epoch)
    {
        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            RedirectUri = "/auth/complete?returnUrl=" + Uri.EscapeDataString(AuthenticationEndpoints.LocalReturnUrl(returnUrl))
        };
        if (Guid.TryParseExact(epoch, "D", out var value) && value != Guid.Empty)
            properties.Items[EpochProperty] = value.ToString("D");
        return properties;
    }
}
