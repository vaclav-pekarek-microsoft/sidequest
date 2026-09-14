using Sidequest.Domain.Model;

namespace Sidequest.Application.Events;

/// <summary>Authorized consent-based Event invitation projection, distinct from a Quest access grant.</summary>
/// <param name="Id">Internal invitation identifier.</param>
/// <param name="EventId">Internal inviting Event identifier.</param>
/// <param name="EventName">Authorized Event display name.</param>
/// <param name="User">Invited person's internal identity projection.</param>
/// <param name="Status">Pending or terminal invitation state.</param>
/// <param name="ExpiresUtc">UTC deadline bounded by seven days and the Event's local end boundary.</param>
public sealed record EventInvitationSummary(Guid Id, Guid EventId, string EventName,
    PersonSummary User, EventInvitationStatus Status, DateTimeOffset ExpiresUtc);
