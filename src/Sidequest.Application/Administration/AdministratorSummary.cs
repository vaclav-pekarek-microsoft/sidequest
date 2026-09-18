namespace Sidequest.Application.Administration;

/// <summary>Explicit administrator assignment with the concurrency evidence needed to remove it.</summary>
/// <param name="UserId">Internal same-tenant account key.</param>
/// <param name="DisplayName">Trusted directory-maintained account label.</param>
/// <param name="Eligible">Whether the assigned account currently retains workforce eligibility.</param>
/// <param name="Version">Assignment rowversion; stale removals fail rather than overwrite changes.</param>
/// <param name="Email">Trusted same-tenant contact address disclosed only to current administrators.</param>
public sealed record AdministratorSummary(Guid UserId, string DisplayName, bool Eligible, byte[] Version, string Email);
