namespace Sidequest.Domain.Rules;

/// <summary>Expected application failure categories safe to map to user-facing outcomes.</summary>
public enum ErrorCode
{
    /// <summary>Input violates a field or business validation rule.</summary>
    Validation,
    /// <summary>Resource is missing or deliberately undisclosed to the actor.</summary>
    NotFound,
    /// <summary>Identity, eligibility, or required global permission is absent.</summary>
    Forbidden,
    /// <summary>Current state or concurrency version prevents the requested change.</summary>
    Conflict,
    /// <summary>A required external dependency is unavailable; the operation must not report success.</summary>
    DependencyUnavailable
}
