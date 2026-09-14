namespace Sidequest.Domain.Model;

/// <summary>Request lifecycle; only approval activates membership.</summary>
public enum MembershipRequestStatus
{
    /// <summary>Awaiting a manager decision; grants no access.</summary>
    Pending,
    /// <summary>Resolved by membership activation.</summary>
    Approved,
    /// <summary>Denied or closed with a reason retained for the requester.</summary>
    Rejected,
    /// <summary>Retracted by the requester.</summary>
    Withdrawn
}
