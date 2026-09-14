using Sidequest.Domain.Model;

namespace Sidequest.Application.Quests;

/// <summary>Authorized Quest projection with privacy-aware nullable participation counts.</summary>
/// <param name="Id">Internal Quest identifier.</param>
/// <param name="EventId">Internal parent Event identifier and membership boundary.</param>
/// <param name="EventName">Authorized parent Event display name.</param>
/// <param name="Title">Activity title.</param>
/// <param name="Location">Activity meeting location.</param>
/// <param name="StartUtc">UTC start instant.</param>
/// <param name="EndUtc">UTC exclusive end instant, strictly after start.</param>
/// <param name="TimeZoneId">IANA zone inherited from the Event, not independently stored or edited on the Quest.</param>
/// <param name="Status">Lifecycle state presented by the query, subject to effective time boundaries.</param>
/// <param name="Visibility">Public/private policy within Event membership.</param>
/// <param name="AttendeeCount">Current Joined count, or null when privacy suppresses counts, such as moderation-only access; null is not zero.</param>
/// <param name="FollowerCount">Current Following count, or null when privacy suppresses counts; null is not zero.</param>
/// <param name="SuggestedCapacity">Optional advisory attendance count, not a joining limit.</param>
/// <param name="Participation">Current actor's exclusive None/Following/Joined state.</param>
/// <param name="IsOwner">Whether the current actor is an equal Quest owner.</param>
/// <param name="CanModerate">Whether the actor can use the separate Event-owner moderation path, not a content-editing grant.</param>
/// <param name="Version">Opaque Base64 SQL rowversion for concurrency checks, not a timestamp.</param>
/// <param name="CoverAssetId">Internal private media identifier, or null without a cover; possession does not authorize retrieval.</param>
public sealed record QuestSummary(Guid Id, Guid EventId, string EventName, string Title, string Location,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId, QuestStatus Status, QuestVisibility Visibility,
    int? AttendeeCount, int? FollowerCount, int? SuggestedCapacity, ParticipationStatus Participation,
    bool IsOwner, bool CanModerate, string Version, Guid? CoverAssetId);
