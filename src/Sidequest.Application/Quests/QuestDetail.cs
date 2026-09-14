using Sidequest.Application.Events;

namespace Sidequest.Application.Quests;

/// <summary>Authorized Quest content with roster disclosure determined independently for ordinary, owner, and moderation views.</summary>
/// <param name="Summary">Privacy-filtered Quest summary.</param>
/// <param name="Description">Authorized plain-text activity details.</param>
/// <param name="StatusReason">Participant-facing lifecycle explanation.</param>
/// <param name="Owners">Equal Quest owners; no primary-owner or creator privilege is implied.</param>
/// <param name="Attendees">Current attendee names for authorized ordinary viewers, or null when withheld for privacy, including moderation-only views.</param>
/// <param name="Followers">Current follower names for Quest owners, or null when the actor may not see the roster.</param>
/// <param name="Invitees">Named invitee roster for Quest owners, or null when the actor may not see it; null is not an empty roster.</param>
/// <remarks>Read-only roster interfaces do not make their backing collections immutable. Do not mutate published collections
/// concurrently with serialization or UI consumption.</remarks>
public sealed record QuestDetail(QuestSummary Summary, string Description, string StatusReason,
    IReadOnlyList<OwnerSummary> Owners, IReadOnlyList<PersonSummary>? Attendees,
    IReadOnlyList<PersonSummary>? Followers, IReadOnlyList<PersonSummary>? Invitees);
