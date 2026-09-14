namespace Sidequest.Domain.Model;

/// <summary>Persisted outcome of one snapshot recipient's bulk operation.</summary>
public enum BulkRecipientStatus
{
    /// <summary>Recipient has not yet reached a recorded outcome.</summary>
    Pending,
    /// <summary>Requested individual operation was applied.</summary>
    Applied,
    /// <summary>No change was needed or a safeguard prevented applying stale work.</summary>
    Skipped,
    /// <summary>Recipient operation failed with inspectable detail.</summary>
    Failed
}
