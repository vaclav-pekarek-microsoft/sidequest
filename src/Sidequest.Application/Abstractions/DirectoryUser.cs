namespace Sidequest.Application.Abstractions;

/// <summary>Trusted directory user result for identity resolution and explicit audience selection, not an Event access grant.</summary>
/// <param name="TenantId">Entra tenant identifier.</param>
/// <param name="ObjectId">Entra user object identifier, not an internal account ID.</param>
/// <param name="DisplayName">Mutable directory display label.</param>
/// <param name="Email">Directory-resolved contact address; unusable values must not be treated as successful email destinations.</param>
/// <param name="IsEligible">Whether directory eligibility policy accepts this workforce account.</param>
public sealed record DirectoryUser(Guid TenantId, Guid ObjectId, string DisplayName, string Email, bool IsEligible);
