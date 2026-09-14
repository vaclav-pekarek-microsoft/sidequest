namespace Sidequest.Application.Abstractions;

/// <summary>Selectable directory group for a one-time expansion, never a membership or ownership grant.</summary>
/// <param name="ObjectId">Entra group object identifier used as an operational bulk input.</param>
/// <param name="DisplayName">Directory display label for group selection.</param>
public sealed record DirectoryGroup(Guid ObjectId, string DisplayName);
