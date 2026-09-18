namespace Sidequest.Application.Administration;

/// <summary>Minimal trusted local-account selection; contains no resource membership or directory roster.</summary>
/// <param name="Id">Internal account key used for commands, never a submitted email address.</param>
/// <param name="DisplayName">Directory-maintained display label.</param>
/// <param name="Version">Opaque account rowversion captured at selection.</param>
/// <param name="Email">Trusted local contact address disclosed only after administrator authorization.</param>
public sealed record AccountChoice(Guid Id, string DisplayName, byte[] Version, string Email);
