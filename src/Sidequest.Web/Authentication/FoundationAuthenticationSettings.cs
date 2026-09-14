namespace Sidequest.Web.Authentication;

public sealed record FoundationAuthenticationSettings(
    bool IsDevelopment, Guid TenantId, string WorkforceRole, Guid? BootstrapAdministratorObjectId)
{
    public const string SyntheticClaim = "sidequest:synthetic";
    public const string CookieScheme = "Cookies";

    public static FoundationAuthenticationSettings Load(IConfiguration configuration, IHostEnvironment environment)
    {
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
