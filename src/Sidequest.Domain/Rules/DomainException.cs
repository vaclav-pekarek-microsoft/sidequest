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
    /// <param name="retryAfter">Optional nonnegative provider delay for a dependency failure; null retains ordinary retry timing.</param>
    /// <exception cref="ArgumentException">A non-dependency category carries dependency-specific retry metadata.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The provider retry delay is negative.</exception>
    public DomainException(ErrorCode code, string message, string? field = null, bool isPermanentDependencyFailure = false,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        if (isPermanentDependencyFailure && code != ErrorCode.DependencyUnavailable)
            throw new ArgumentException("Only dependency-unavailable failures may be marked as permanent dependencies.", nameof(isPermanentDependencyFailure));
        if (retryAfter is not null && code != ErrorCode.DependencyUnavailable)
            throw new ArgumentException("Provider retry delays apply only to dependency-unavailable failures.", nameof(retryAfter));
        if (retryAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryAfter), "Provider retry delays must not be negative.");
        Code = code;
        Field = field;
        IsPermanentDependencyFailure = isPermanentDependencyFailure;
        RetryAfter = retryAfter;
    }

    /// <summary>Stable category distinguishing validation, access, state, and dependency failures.</summary>
    public ErrorCode Code { get; }
    /// <summary>Associated input field, or null when the failure is not field-specific.</summary>
    public string? Field { get; }
    /// <summary>Whether a dependency requires operator correction before replay; unmarked failures retain their existing retry policy.</summary>
    public bool IsPermanentDependencyFailure { get; }
    /// <summary>Minimum provider-requested wait before automatic retry, or null when no valid delay was supplied.</summary>
    public TimeSpan? RetryAfter { get; }
}
