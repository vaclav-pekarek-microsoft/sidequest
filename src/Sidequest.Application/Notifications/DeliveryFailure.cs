namespace Sidequest.Application.Notifications;

/// <summary>Administrator-visible redacted durable-work failure available for investigation and authorized replay.</summary>
/// <param name="Id">Internal identifier of the failed delivery/work record.</param>
/// <param name="Kind">Replay discriminator identifying the durable record category, not a notification content authorization grant.</param>
/// <param name="Error">Redacted actionable failure detail without secrets or protected message bodies.</param>
/// <param name="Attempts">Recorded processing attempt count.</param>
/// <param name="DueUtc">UTC due instant recorded for the work; dead-letter status does not imply an automatic future retry.</param>
public sealed record DeliveryFailure(Guid Id, string Kind, string Error, int Attempts, DateTimeOffset DueUtc);
