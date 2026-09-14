namespace Sidequest.Application.Abstractions;

/// <summary>Authenticated external identity snapshot; does not itself establish current application eligibility or resource access.</summary>
/// <param name="TenantId">Entra tenant identifier forming an external key with ObjectId.</param>
/// <param name="ObjectId">Entra user object identifier, not the application's internal UserAccount.Id.</param>
/// <param name="DisplayName">Mutable display data, never an authorization key.</param>
/// <param name="Email">Mutable contact data, never an authorization key.</param>
public sealed record UserIdentity(Guid TenantId, Guid ObjectId, string DisplayName, string Email);
