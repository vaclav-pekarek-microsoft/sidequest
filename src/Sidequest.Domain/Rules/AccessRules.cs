using Sidequest.Domain.Model;

namespace Sidequest.Domain.Rules;

/// <summary>Pure read/moderation predicates over previously resolved database authorization facts; no identity lookup or mutation occurs.</summary>
/// <remarks>Methods use only value inputs and are safe for concurrent calls. Callers are responsible for obtaining a consistent,
/// current authorization snapshot; thread safety does not make stale access facts authoritative.</remarks>
public static class AccessRules
{
    /// <summary>Evaluates full Event read access; discovery summaries require a separate disclosure policy.</summary>
    /// <param name="eligible">Whether the actor is currently eligible and not departed.</param>
    /// <param name="member">Whether the actor has active individual Event membership.</param>
    /// <param name="owner">Whether the actor is an equal Event owner.</param>
    /// <param name="status">Event lifecycle state.</param>
    /// <returns>True only for eligible members, with ownership additionally required for Draft Events.</returns>
    public static bool CanReadEvent(bool eligible, bool member, bool owner, EventStatus status) =>
        eligible && member && (status != EventStatus.Draft || owner);

    /// <summary>Evaluates ordinary Quest reads, not the separate Event-owner moderation exception.</summary>
    /// <param name="eligible">Whether the actor is currently eligible and not departed.</param>
    /// <param name="member">Whether the actor has active individual parent Event membership.</param>
    /// <param name="owner">Whether the actor is a Quest owner.</param>
    /// <param name="invited">Whether the actor has an active named Quest invitation.</param>
    /// <param name="eventStatus">Parent Event lifecycle state.</param>
    /// <param name="questStatus">Quest lifecycle state.</param>
    /// <param name="visibility">Ordinary member visibility policy.</param>
    /// <returns>True when eligibility, membership, draft restrictions, and owner/invitation/public access permit reading.</returns>
    public static bool CanReadQuest(bool eligible, bool member, bool owner, bool invited,
        EventStatus eventStatus, QuestStatus questStatus, QuestVisibility visibility) =>
        eligible && member && eventStatus != EventStatus.Draft &&
        (questStatus == QuestStatus.Draft ? owner : owner || invited || visibility == QuestVisibility.Public);

    /// <summary>Evaluates Event-owner moderation access to non-draft Quests, including private Quests without roster disclosure.</summary>
    /// <param name="eligible">Whether the actor is currently eligible and not departed.</param>
    /// <param name="member">Whether the actor has active individual Event membership.</param>
    /// <param name="eventOwner">Whether the actor is an equal owner of the parent Event.</param>
    /// <param name="status">Quest lifecycle state; drafts are excluded.</param>
    /// <returns>True only for eligible member Event owners viewing non-draft Quests.</returns>
    public static bool CanModerate(bool eligible, bool member, bool eventOwner, QuestStatus status) =>
        eligible && member && eventOwner && status != QuestStatus.Draft;
}
