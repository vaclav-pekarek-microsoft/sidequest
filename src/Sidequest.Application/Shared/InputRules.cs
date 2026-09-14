using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Shared;

public static class InputRules
{
    public static string Text(string? value, string field, int minimum, int maximum)
    {
        var text = value?.Trim() ?? "";
        if (text.Length < minimum || text.Length > maximum)
            throw new DomainException(ErrorCode.Validation, $"{field} must contain {minimum} to {maximum} characters.", field);
        return text;
    }

    public static string Reason(string? value) => Text(value, "Reason", 10, 2000);

    public static void Capacity(int? value)
    {
        if (value is < 1 or > 10000)
            throw new DomainException(ErrorCode.Validation, "Suggested capacity must be between 1 and 10000.", "SuggestedCapacity");
    }

    public static void Version(Entity entity, string expected)
    {
        Span<byte> decoded = stackalloc byte[8];
        if (!Convert.TryFromBase64String(expected, decoded, out var length) || length != 8)
            throw new DomainException(ErrorCode.Validation, "A valid item version is required.", "Version");
        if (!entity.Version.AsSpan().SequenceEqual(decoded))
            throw new DomainException(ErrorCode.Conflict, "This item changed. Reload before saving.");
    }
}
