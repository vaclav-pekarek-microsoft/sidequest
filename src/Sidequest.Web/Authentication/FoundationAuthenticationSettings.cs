namespace Sidequest.Web.Authentication;

/// <summary>Holds the validated, immutable admission and one-time administrator bootstrap settings.</summary>
/// <param name="IsDevelopment">Whether explicitly enabled synthetic development authentication is selected.</param>
/// <param name="TenantId">The only tenant permitted to supply identities.</param>
/// <param name="WorkforceRole">The exact admission app-role claim value; it grants no database application role.</param>
/// <param name="BootstrapAdministratorObjectId">The explicitly configured object to bootstrap on first provisioning, or no bootstrap.</param>
/// <remarks>Validated instances are immutable and safe to share across requests and circuits.</remarks>
public sealed record FoundationAuthenticationSettings(
    bool IsDevelopment, Guid TenantId, string WorkforceRole, Guid? BootstrapAdministratorObjectId)
{
    /// <summary>The claim type that distinguishes synthetic identities from Entra identities.</summary>
    public const string SyntheticClaim = "sidequest:synthetic";
    /// <summary>The authentication scheme used for the application session cookie in either mode.</summary>
    public const string CookieScheme = "Cookies";

    /// <summary>Loads startup settings, defaulting to Entra and rejecting unsafe or incomplete configuration.</summary>
    /// <param name="configuration">The merged configuration, including secret-store values for Entra.</param>
    /// <param name="environment">The host environment used to forbid synthetic authentication outside Development.</param>
    /// <returns>Validated settings for one authentication mode and tenant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="environment"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The mode, tenant/client credentials, workforce role, or bootstrap identity is invalid.</exception>
    /// <example>
    /// <code>
    /// var settings = FoundationAuthenticationSettings.Load(builder.Configuration, builder.Environment);
    /// builder.Services.AddSingleton(settings);
    /// </code>
    /// </example>
    public static FoundationAuthenticationSettings Load(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var mode = configuration["Authentication:Mode"] ?? "Entra";
        if (mode is not ("Entra" or "Development"))
            throw new InvalidOperationException("Authentication:Mode must be Entra or Development.");
        if (mode == "Development")
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Synthetic authentication is allowed only in the Development environment.");
            var bootstrap = BootstrapAdministrator(configuration, DevelopmentPersonas.TenantId);
            if (bootstrap is not null && bootstrap != DevelopmentPersonas.All[0].ObjectId)
                throw new InvalidOperationException("Development bootstrap administrator must be the documented Admin object.");
            return new(true, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole,
                bootstrap);
        }

        var tenant = RequiredGuid(configuration["AzureAd:TenantId"], "AzureAd:TenantId");
        _ = RequiredGuid(configuration["AzureAd:ClientId"], "AzureAd:ClientId");
        if (string.IsNullOrWhiteSpace(configuration["AzureAd:ClientSecret"]))
            throw new InvalidOperationException("Configure AzureAd:ClientSecret using user secrets or the deployment secret store.");
        if (configuration["AzureAd:Instance"] is { } instance &&
            instance != "https://login.microsoftonline.com/")
            throw new InvalidOperationException("AzureAd:Instance must be https://login.microsoftonline.com/.");
        var role = configuration["Authentication:WorkforceRole"];
        if (string.IsNullOrWhiteSpace(role))
            throw new InvalidOperationException("Authentication:WorkforceRole must name the approved workforce-only app role.");
        return new(false, tenant, role, BootstrapAdministrator(configuration, tenant));
    }

    private static Guid? BootstrapAdministrator(IConfiguration configuration, Guid tenant)
    {
        Guid? administrator = null;
        if (configuration["Authentication:BootstrapAdministrator:ObjectId"] is { Length: > 0 } objectId)
        {
            administrator = RequiredGuid(objectId, "Authentication:BootstrapAdministrator:ObjectId");
            var administratorTenant = RequiredGuid(configuration["Authentication:BootstrapAdministrator:TenantId"],
                "Authentication:BootstrapAdministrator:TenantId");
            if (administratorTenant != tenant)
                throw new InvalidOperationException("Bootstrap administrator must belong to the configured tenant.");
        }
        else if (!string.IsNullOrEmpty(configuration["Authentication:BootstrapAdministrator:TenantId"]))
            throw new InvalidOperationException("Bootstrap administrator requires both TenantId and ObjectId.");
        return administrator;
    }

    private static Guid RequiredGuid(string? value, string name) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new InvalidOperationException($"{name} must be a nonempty tenant-specific GUID.");
}
