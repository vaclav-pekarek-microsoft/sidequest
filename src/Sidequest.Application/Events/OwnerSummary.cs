namespace Sidequest.Application.Events;

/// <summary>Equal-owner directory contact projection; ordering does not imply a primary owner.</summary>
/// <param name="Id">Internal owner account identifier.</param>
/// <param name="DisplayName">Owner's directory-derived display label.</param>
/// <param name="Email">Owner's directory contact address, disclosed only where owner-contact policy allows.</param>
public sealed record OwnerSummary(Guid Id, string DisplayName, string Email);
