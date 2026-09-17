namespace Sidequest.Web.Hosting;

/// <summary>Validated, immutable configuration for explicit managed-identity Azure hosting, never a cloud approval.</summary>
public sealed class AzureHostingSettings
{
    private AzureHostingSettings(string applicationName, Uri blobUri, Uri keyUri, string telemetryConnectionString)
    {
        ApplicationName = applicationName;
        BlobUri = blobUri;
        KeyUri = keyUri;
        TelemetryConnectionString = telemetryConnectionString;
    }

    /// <summary>Stable Data Protection discriminator shared by restarts of this deployment, but not unrelated environments.</summary>
    public string ApplicationName { get; }

    /// <summary>HTTPS URI of the private key-ring blob, without credentials, SAS, query or fragment.</summary>
    public Uri BlobUri { get; }

    /// <summary>Versionless Azure Key Vault key URI; retained older versions remain necessary to decrypt existing key rings.</summary>
    public Uri KeyUri { get; }

    /// <summary>Application Insights routing connection string; managed identity, not the instrumentation key, authenticates ingestion.</summary>
    public string TelemetryConnectionString { get; }

    /// <summary>Loads the opt-in Azure hosting configuration before any client registration or network operation.</summary>
    /// <param name="configuration">Deployment configuration, with secrets supplied outside source control.</param>
    /// <param name="environment">Host environment; synthetic Development hosts cannot opt in to cloud identity.</param>
    /// <returns>Validated settings when enabled, or null when the Azure hosting flag is absent or explicitly false.</returns>
    /// <remarks>The exporter validates telemetry connection-string syntax during host initialization.</remarks>
    /// <exception cref="ArgumentNullException">Configuration or environment is null.</exception>
    /// <exception cref="InvalidOperationException">The flag is malformed, enabled hosting is synthetic, or required settings are invalid.</exception>
    public static AzureHostingSettings? Load(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var flag = configuration["Hosting:Azure:Enabled"];
        if (flag is null)
            return null;
        if (!bool.TryParse(flag, out var enabled))
            throw new InvalidOperationException("Hosting:Azure:Enabled must be true or false.");
        if (!enabled)
            return null;
        if (environment.IsDevelopment() || !string.Equals(configuration["Authentication:Mode"], "Entra", StringComparison.Ordinal))
            throw new InvalidOperationException("Azure hosting requires a non-Development host with explicit Entra authentication.");

        var name = Required(configuration, "Hosting:DataProtection:ApplicationName");
        if (name.Length > 128 || name != name.Trim())
            throw new InvalidOperationException("The Data Protection application name must be 1 to 128 characters without surrounding whitespace.");
        var blob = Endpoint(configuration, "Hosting:DataProtection:BlobUri",
            [".blob.core.windows.net", ".blob.core.usgovcloudapi.net", ".blob.core.chinacloudapi.cn"]);
        if (blob.Segments.Length < 3 || blob.AbsolutePath.EndsWith('/'))
            throw new InvalidOperationException("The Data Protection blob URI must identify a container and a key-ring blob.");
        var key = Endpoint(configuration, "Hosting:DataProtection:KeyUri",
            [".vault.azure.net", ".vault.usgovcloudapi.net", ".vault.azure.cn"]);
        if (key.Segments.Length != 3 || key.Segments[1] != "keys/" || key.Segments[2].Length is < 1 or > 127 ||
            !key.Segments[2].All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw new InvalidOperationException("The Data Protection wrapping key must use a versionless /keys/name URI.");
        return new(name, blob, key, Required(configuration, "APPLICATIONINSIGHTS_CONNECTION_STRING"));
    }

    private static string Required(IConfiguration configuration, string name) =>
        !string.IsNullOrWhiteSpace(configuration[name]) ? configuration[name]! :
            throw new InvalidOperationException($"Azure hosting requires {name}.");

    private static Uri Endpoint(IConfiguration configuration, string name, string[] suffixes)
    {
        if (!Uri.TryCreate(Required(configuration, name), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !suffixes.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && uri.Host.Length > suffix.Length))
            throw new InvalidOperationException($"{name} requires a native Azure HTTPS endpoint without credentials, query or fragment.");
        return uri;
    }
}
