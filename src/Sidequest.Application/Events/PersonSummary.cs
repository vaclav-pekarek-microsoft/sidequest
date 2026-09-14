namespace Sidequest.Application.Events;

/// <summary>Minimal named-user projection for rosters without email disclosure.</summary>
/// <param name="Id">Internal account identifier, not an Entra object identifier.</param>
/// <param name="DisplayName">Directory-derived display label.</param>
public sealed record PersonSummary(Guid Id, string DisplayName);
