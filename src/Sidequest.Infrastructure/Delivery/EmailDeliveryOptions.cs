namespace Sidequest.Infrastructure.Delivery;

/// <summary>Deployment-only ACS and calendar organizer settings; secrets must come from configuration backed by a secret store.</summary>
public sealed class EmailDeliveryOptions
{
    /// <summary>Allowlisted ACS verified sending address, also the stable calendar organizer; never a Quest owner's contact.</summary>
    public string SenderAddress { get; set; } = "";
    /// <summary>Secret-store supplied ACS connection string, or null to use managed identity with Endpoint.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>HTTPS ACS endpoint for managed identity authentication; null means unconfigured.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Optional user-assigned managed identity client ID; null selects the host's system identity.</summary>
    public string? ManagedIdentityClientId { get; set; }
    /// <summary>Maximum time awaiting ACS submission/status before retaining an uncertain outcome; defaults to sixty seconds.</summary>
    public TimeSpan SubmissionTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
