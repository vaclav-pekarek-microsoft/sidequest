namespace Sidequest.Domain.Rules;

/// <summary>Expected business failure carrying a stable category and optional input-field association.</summary>
public sealed class DomainException : Exception
{
    /// <summary>Creates an expected failure while preserving its presentation category independently of dependency retry policy.</summary>
    /// <param name="code">Category used to present or translate the failure.</param>
    /// <param name="message">Safe user-facing explanation without protected resource details.</param>
    /// <param name="field">Associated input field, or null for a non-field failure.</param>
    /// <param name="isPermanentDependencyFailure">True only for a dependency failure requiring operator correction rather than automatic retry.
    /// False preserves existing retry classification; the flag never changes the user-facing dependency category.</param>
    /// <exception cref="ArgumentException">A non-dependency category is incorrectly marked as a permanent dependency failure.</exception>
    public DomainException(ErrorCode code, string message, string? field = null, bool isPermanentDependencyFailure = false)
        : base(message)
    {
        if (isPermanentDependencyFailure && code != ErrorCode.DependencyUnavailable)
            throw new ArgumentException("Only dependency-unavailable failures may be marked as permanent dependencies.", nameof(isPermanentDependencyFailure));
        Code = code;
        Field = field;
        IsPermanentDependencyFailure = isPermanentDependencyFailure;
    }

    /// <summary>Stable category distinguishing validation, access, state, and dependency failures.</summary>
    public ErrorCode Code { get; }
    /// <summary>Associated input field, or null when the failure is not field-specific.</summary>
    public string? Field { get; }
    /// <summary>Whether a dependency requires operator correction before replay; unmarked failures retain their existing retry policy.</summary>
    public bool IsPermanentDependencyFailure { get; }
}
