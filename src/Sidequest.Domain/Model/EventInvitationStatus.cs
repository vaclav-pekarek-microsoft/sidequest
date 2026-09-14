namespace Sidequest.Domain.Model;

/// <summary>Consent-based Event invitation lifecycle, distinct from a Quest access grant.</summary>
public enum EventInvitationStatus
{
    /// <summary>Awaiting response before the invitation deadline.</summary>
    Pending,
    /// <summary>Resolved by membership activation, including an explicit direct add.</summary>
    Accepted,
    /// <summary>Recipient chose not to accept membership.</summary>
    Declined,
    /// <summary>Manager withdrew the outstanding invitation.</summary>
    Revoked,
    /// <summary>Deadline or Event lifecycle no longer permits acceptance.</summary>
    Expired
}
