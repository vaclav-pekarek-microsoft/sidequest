using Sidequest.Domain.Model;

namespace Sidequest.Application.Quests;

/// <summary>Minimal joined-Quest offline display snapshot; never an authorization source or offline mutation instruction.</summary>
/// <param name="Id">Internal joined Quest identifier.</param>
/// <param name="EventId">Internal parent Event identifier.</param>
/// <param name="Title">Last refreshed activity title.</param>
/// <param name="Location">Last refreshed meeting location.</param>
/// <param name="StartUtc">Last refreshed UTC start instant.</param>
/// <param name="EndUtc">Last refreshed UTC end instant.</param>
/// <param name="TimeZoneId">Last refreshed inherited Event IANA zone.</param>
/// <param name="Status">Last known lifecycle state, not a guarantee of current online state.</param>
public sealed record OfflineQuest(Guid Id, Guid EventId, string Title, string Location,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId, QuestStatus Status);
