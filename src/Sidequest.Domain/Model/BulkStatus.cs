namespace Sidequest.Domain.Model;

/// <summary>One-time directory expansion and application workflow state.</summary>
public enum BulkStatus
{
    /// <summary>Enumerating and deduplicating all recipients before any membership changes.</summary>
    Expanding,
    /// <summary>Applying ordinary individual operations against the saved recipient snapshot.</summary>
    Applying,
    /// <summary>Recipient processing finished; individual skipped or failed outcomes remain inspectable.</summary>
    Completed,
    /// <summary>Workflow failed with an actionable recorded error.</summary>
    Failed
}
