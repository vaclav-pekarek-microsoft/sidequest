namespace Sidequest.Web.Authentication;

/// <summary>Holds validated magic-code or synthetic admission and one-time administrator bootstrap settings.</summary>
/// <param name="IsDevelopment">Whether explicitly enabled synthetic development authentication is selected.</param>
/// <param name="TenantId">The stable account namespace shared by authentication and directory integrations.</param>
/// <param name="WorkforceRole">The exact application-issued admission claim; it grants no database application role.</param>
/// <param name="BootstrapAdministratorObjectId">The explicit synthetic object to bootstrap, or no object-based bootstrap.</param>
/// <remarks>Validated instances are immutable and safe to share across requests and circuits.</remarks>
public sealed record FoundationAuthenticationSettings(
    bool IsDevelopment, Guid TenantId, string WorkforceRole, Guid? BootstrapAdministratorObjectId)
{
    /// <summary>The normalized Microsoft mailbox that may receive a one-time administrator grant when first created.</summary>
    public string? BootstrapAdministratorEmail { get; private init; }

    /// <summary>Whether deployed Microsoft-alias magic-code authentication is selected.</summary>
    public bool IsMagicCode => !IsDevelopment;

    /// <summary>The claim type that distinguishes synthetic identities from deployed identities.</summary>
    public const string SyntheticClaim = "sidequest:synthetic";

    /// <summary>The claim type proving that the application issued the identity after successful code verification.</summary>
    public const string MagicCodeClaim = "sidequest:magic-code";

    /// <summary>The fixed application admission role issued only after successful Microsoft-mailbox verification.</summary>
    public const string MagicCodeRole = "Sidequest.MagicCodeWorkforce";

    /// <summary>The authentication scheme used for the application session cookie in either mode.</summary>
    public const string CookieScheme = "Cookies";

    /// <summary>Loads startup settings, defaulting to deployed magic-code authentication and rejecting unsafe configuration.</summary>
    /// <param name="configuration">The merged configuration containing the stable account namespace and optional bootstrap alias.</param>
    /// <param name="environment">The host environment restricting synthetic authentication to Development.</param>
    /// <returns>Validated settings for exactly one authentication mode.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="environment"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The mode, account namespace, or bootstrap identity is invalid.</exception>
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
        var mode = configuration["Authentication:Mode"] ?? "MagicCode";
        if (mode is not ("MagicCode" or "Development"))
            throw new InvalidOperationException("Authentication:Mode must be MagicCode or Development.");
        if (mode == "Development")
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Synthetic authentication is allowed only in the Development environment.");
            var bootstrap = BootstrapAdministrator(configuration, DevelopmentPersonas.TenantId);
            if (bootstrap is not null && bootstrap != DevelopmentPersonas.All[0].ObjectId)
                throw new InvalidOperationException("Development bootstrap administrator must be the documented Admin object.");
            return new(true, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, bootstrap);
        }

        var tenant = RequiredGuid(configuration["Authentication:AccountNamespaceId"], "Authentication:AccountNamespaceId");
        string? bootstrapEmail = null;
        if (configuration["Authentication:BootstrapAdministrator:Alias"] is { Length: > 0 } alias)
        {
            try
            {
                bootstrapEmail = MagicAlias.Normalize(alias);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    "Authentication:BootstrapAdministrator:Alias must be a valid Microsoft alias without a domain.", exception);
            }
        }
        if (!string.IsNullOrEmpty(configuration["Authentication:BootstrapAdministrator:TenantId"]) ||
            !string.IsNullOrEmpty(configuration["Authentication:BootstrapAdministrator:ObjectId"]))
            throw new InvalidOperationException(
                "MagicCode authentication bootstraps administrators by Authentication:BootstrapAdministrator:Alias.");
        return new(false, tenant, MagicCodeRole, null) { BootstrapAdministratorEmail = bootstrapEmail };
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
            : throw new InvalidOperationException($"{name} must be a nonempty GUID.");
}
