namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Safe transport outcome classification; never contains raw provider bodies, addresses, or credentials.</summary>
/// <param name="outcome">Whether failure is permanent, retryable, or potentially submitted.</param>
/// <param name="safeCode">Allowlisted operational diagnostic code.</param>
/// <param name="retryAfter">Optional provider retry delay.</param>
public sealed class DeliveryTransportException(TransportOutcome outcome, string safeCode, TimeSpan? retryAfter = null)
    : Exception(safeCode)
{
    /// <summary>Provider submission certainty and retry classification.</summary>
    public TransportOutcome Outcome { get; } = outcome;
    /// <summary>Provider requested minimum retry delay, or null.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
