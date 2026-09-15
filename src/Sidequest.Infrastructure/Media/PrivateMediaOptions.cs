namespace Sidequest.Infrastructure.Media;

/// <summary>Deployment-only settings for a preprovisioned private Azure Blob container; no provider I/O occurs during binding.</summary>
public sealed class PrivateMediaOptions
{
    /// <summary>HTTPS Blob service endpoint for managed identity authentication, without query strings or SAS.</summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>Optional user-assigned managed identity client ID; null selects the host's system-assigned identity.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Secret-store connection string alternative to managed identity; never commit a value to source control.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Name of an existing container whose public access policy must be disabled for every operation.</summary>
    public string ContainerName { get; set; } = "";

    /// <summary>Maximum cooperative provider duration, positive and no more than sixty seconds; defaults to thirty seconds.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
