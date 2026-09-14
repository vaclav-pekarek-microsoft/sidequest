namespace Sidequest.Infrastructure.Directory;

/// <summary>Optional client-credential token configuration, supplied through protected deployment configuration only.</summary>
/// <remarks>Hosts using managed identity may instead supply their own IGraphAccessTokenProvider; do not commit credentials.</remarks>
public sealed class GraphClientCredentialOptions
{
    /// <summary>Configured single workforce tenant; must match GraphDirectoryOptions.TenantId.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Application registration granted approved Graph application permissions.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Deployment-secret credential; never logged, returned to a browser, or used as a fallback identity.</summary>
    public string ClientSecret { get; init; } = "";
}
