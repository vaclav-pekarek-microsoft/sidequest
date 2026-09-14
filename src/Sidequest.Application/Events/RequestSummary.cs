using Sidequest.Domain.Model;

namespace Sidequest.Application.Events;

/// <summary>Authorized membership request projection with retained decision information.</summary>
/// <param name="Id">Internal request identifier.</param>
/// <param name="EventId">Internal requested Event identifier.</param>
/// <param name="EventName">Authorized Event display name.</param>
/// <param name="User">Requester identity without email disclosure.</param>
/// <param name="Status">Pending, decided, or withdrawn state.</param>
/// <param name="Reason">Recorded decision explanation, including rejection reasons.</param>
/// <param name="CreatedUtc">UTC instant when the request was submitted.</param>
public sealed record RequestSummary(Guid Id, Guid EventId, string EventName, PersonSummary User,
    MembershipRequestStatus Status, string Reason, DateTimeOffset CreatedUtc);
