namespace Sidequest.Domain.Rules;

/// <summary>Expected business failure carrying a stable category and optional input-field association.</summary>
/// <param name="code">Category used to present or translate the failure.</param>
/// <param name="message">Safe user-facing explanation without protected resource details.</param>
/// <param name="field">Associated input field, or null for a non-field failure.</param>
public sealed class DomainException(ErrorCode code, string message, string? field = null) : Exception(message)
{
    /// <summary>Stable category distinguishing validation, access, state, and dependency failures.</summary>
    public ErrorCode Code { get; } = code;
    /// <summary>Associated input field, or null when the failure is not field-specific.</summary>
    public string? Field { get; } = field;
}
