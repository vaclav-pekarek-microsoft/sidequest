namespace Sidequest.Application.Abstractions;

/// <summary>Transaction-reserved request facts used to deduplicate admission requests and enforce their hourly limit.</summary>
/// <param name="HasPendingRequest">Whether this Event/account pair has any pending request, regardless of age.</param>
/// <param name="RecentRequestCount">Number of requests in every status created at or after the caller's inclusive cutoff.</param>
public sealed record MembershipRequestState(bool HasPendingRequest, int RecentRequestCount);
