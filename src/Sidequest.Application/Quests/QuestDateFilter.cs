namespace Sidequest.Application.Quests;

/// <summary>Optional instant boundaries for filtering authorized Quests by their start time before counting or paging.</summary>
/// <param name="FromUtc">Inclusive lower start-instant bound, or null for no lower bound.</param>
/// <param name="UntilUtc">Exclusive upper start-instant bound, or null for no upper bound; must follow FromUtc when both exist.</param>
/// <remarks>Boundaries represent UTC instants. UI date ranges are converted using the selected Event's zone,
/// or explicitly labeled UTC when no Event is selected. Filtering never changes stored schedules or authorization.</remarks>
public sealed record QuestDateFilter(DateTimeOffset? FromUtc = null, DateTimeOffset? UntilUtc = null);
