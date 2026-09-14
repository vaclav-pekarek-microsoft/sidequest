using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Shared;

/// <summary>Shared input normalization and validation without persistence or authorization side effects.</summary>
/// <remarks>Text and capacity validation have no shared mutable state. Version checks require an entity and rowversion
/// array that are not being concurrently modified; they do not replace transactional concurrency enforcement.</remarks>
public static class InputRules
{
    /// <summary>Trims text, converts null to empty, and checks inclusive UTF-16 length bounds.</summary>
    /// <param name="value">Input text, or null to normalize as empty.</param>
    /// <param name="field">Field name associated with validation failures.</param>
    /// <param name="minimum">Inclusive minimum normalized string length in UTF-16 code units.</param>
    /// <param name="maximum">Inclusive maximum normalized string length in UTF-16 code units.</param>
    /// <returns>Trimmed text when its length is within the supplied bounds.</returns>
    /// <exception cref="DomainException">Normalized length falls outside the supplied bounds (Validation on field).</exception>
    public static string Text(string? value, string field, int minimum, int maximum)
    {
        var text = value?.Trim() ?? "";
        if (text.Length < minimum || text.Length > maximum)
            throw new DomainException(ErrorCode.Validation, $"{field} must contain {minimum} to {maximum} characters.", field);
        return text;
    }

    /// <summary>Normalizes a mandatory action reason and enforces a length of 10 through 2000 UTF-16 code units.</summary>
    /// <param name="value">Reason text; null normalizes to empty and fails validation.</param>
    /// <returns>The trimmed reason.</returns>
    /// <exception cref="DomainException">The trimmed reason is outside the inclusive length bounds (Validation on Reason).</exception>
    public static string Reason(string? value) => Text(value, "Reason", 10, 2000);

    /// <summary>Validates advisory capacity without imposing a hard attendance limit.</summary>
    /// <param name="value">Suggested attendee count from 1 through 10000, or null for no suggestion.</param>
    /// <exception cref="DomainException">A non-null value falls outside the inclusive bounds (Validation on SuggestedCapacity).</exception>
    public static void Capacity(int? value)
    {
        if (value is < 1 or > 10000)
            throw new DomainException(ErrorCode.Validation, "Suggested capacity must be between 1 and 10000.", "SuggestedCapacity");
    }

    /// <summary>Validates and compares an opaque eight-byte SQL rowversion token; does not replace database concurrency enforcement.</summary>
    /// <param name="entity">Persisted entity containing the current binary rowversion.</param>
    /// <param name="expected">Client's Base64-encoded rowversion; null, blank, malformed, and non-eight-byte values are invalid.</param>
    /// <exception cref="DomainException">Token format is invalid (Validation), or its bytes differ from the entity's version (Conflict).</exception>
    /// <example>
    /// <code>
    /// // persistedEntity was freshly loaded and authorized in the current operation.
    /// InputRules.Version(persistedEntity, submittedVersion);
    /// // Apply the edit only after this check; EF still detects changes racing the eventual save.
    /// </code>
    /// </example>
    public static void Version(Entity entity, string? expected)
    {
        Span<byte> decoded = stackalloc byte[8];
        if (string.IsNullOrWhiteSpace(expected) ||
            !Convert.TryFromBase64String(expected, decoded, out var length) || length != 8)
            throw new DomainException(ErrorCode.Validation, "A valid item version is required.", "Version");
        if (!entity.Version.AsSpan().SequenceEqual(decoded))
            throw new DomainException(ErrorCode.Conflict, "This item changed. Reload before saving.");
    }
}
