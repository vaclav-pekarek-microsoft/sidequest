namespace Sidequest.Infrastructure.Background;

/// <summary>One scoped execution's lease proof, populated by the worker and consumed by owned handlers.</summary>
public sealed class WorkExecutionContext
{
    /// <summary>Current ownership proof, or null outside worker execution; setting it does not create a SQL claim.</summary>
    public WorkLease? Lease { get; set; }
}
