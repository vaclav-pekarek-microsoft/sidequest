using Sidequest.Domain.Model;

namespace Sidequest.Application.Events;

/// <summary>Authorized individual audience row, without group-derived grants.</summary>
/// <param name="User">Member identity projected without email.</param>
/// <param name="Status">Active or removed individual membership.</param>
/// <param name="IsOwner">Whether the person also holds equal Event ownership.</param>
public sealed record MembershipSummary(PersonSummary User, MembershipStatus Status, bool IsOwner);
