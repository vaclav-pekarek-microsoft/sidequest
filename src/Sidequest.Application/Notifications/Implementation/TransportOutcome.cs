namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Failure partitions distinct from a provider-issued acceptance receipt.</summary>
public enum TransportOutcome
{
    /// <summary>Correct configuration or invalid recipient before administrator replay.</summary>
    Permanent,
    /// <summary>A definite rejected submission that may be retried.</summary>
    Retryable,
    /// <summary>Submission may have reached the provider; retain withdrawal obligations.</summary>
    Uncertain
}
