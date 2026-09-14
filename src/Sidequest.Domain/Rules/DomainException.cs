namespace Sidequest.Domain.Rules;

public enum ErrorCode { Validation, NotFound, Forbidden, Conflict, DependencyUnavailable }

public sealed class DomainException(ErrorCode code, string message, string? field = null) : Exception(message)
{
    public ErrorCode Code { get; } = code;
    public string? Field { get; } = field;
}
