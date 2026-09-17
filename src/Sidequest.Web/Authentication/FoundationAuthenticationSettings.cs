using System.Collections.Frozen;

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
    /// <summary>Explicit non-production participants; empty for the unchanged workforce admission policy.</summary>
    public IReadOnlySet<Guid> HackathonParticipants { get; private init; } = Array.Empty<Guid>().ToFrozenSet();

    /// <summary>Whether validated Staging or local Development Entra configuration selected assigned-participant rather than workforce admission.</summary>
    public bool IsHackathon => HackathonParticipants.Count != 0;

    /// <summary>The claim type that distinguishes synthetic identities from Entra identities.</summary>
    public const string SyntheticClaim = "sidequest:synthetic";
    /// <summary>The authentication scheme used for the application session cookie in either mode.</summary>
    public const string CookieScheme = "Cookies";

    /// <summary>Loads startup settings, defaulting to Entra and rejecting unsafe or incomplete configuration.</summary>
    /// <param name="configuration">The merged configuration, including secret-store values for Entra.</param>
    /// <param name="environment">The host environment restricting synthetic authentication to Development and Entra assigned-participant admission to Staging or Development.</param>
    /// <returns>Validated settings for one authentication mode and tenant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="environment"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The mode, tenant/client credentials, admission policy/role, participant list, or bootstrap identity is invalid.</exception>
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
        var policy = configuration["Authentication:AdmissionPolicy"] ?? "workforce";
        if (policy is not ("workforce" or "hackathon-assigned-users"))
            throw new InvalidOperationException("Authentication:AdmissionPolicy must be workforce or hackathon-assigned-users.");
        if (policy == "hackathon-assigned-users" &&
            ((!environment.IsStaging() && !environment.IsDevelopment()) || mode != "Entra"))
            throw new InvalidOperationException("Hackathon admission requires Entra authentication in Staging or local Development.");
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
        var hackathon = policy == "hackathon-assigned-users";
        var role = configuration[hackathon ? "Authentication:HackathonRole" : "Authentication:WorkforceRole"];
        if (string.IsNullOrWhiteSpace(role))
            throw new InvalidOperationException(hackathon
                ? "Authentication:HackathonRole must name the dedicated assigned-participant app role."
                : "Authentication:WorkforceRole must name the approved workforce-only app role.");
        if (hackathon && role == configuration["Authentication:WorkforceRole"])
            throw new InvalidOperationException("The hackathon role must be distinct from the workforce role.");
        var participants = hackathon
            ? (configuration.GetSection("Authentication:HackathonParticipants").Get<string[]>() ?? [])
                .Select(value => RequiredGuid(value, "Authentication:HackathonParticipants")).ToFrozenSet()
            : Array.Empty<Guid>().ToFrozenSet();
        if (hackathon && participants.Count is < 1 or > 100)
            throw new InvalidOperationException("Authentication:HackathonParticipants requires 1 to 100 explicitly approved object IDs.");
        var administrator = BootstrapAdministrator(configuration, tenant);
        if (hackathon && administrator is { } id && !participants.Contains(id))
            throw new InvalidOperationException("The hackathon bootstrap administrator must be an explicitly approved participant.");
        return new(false, tenant, role, administrator) { HackathonParticipants = participants };
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
