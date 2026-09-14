using Sidequest.Domain.Model;

namespace Sidequest.Application.Quests;

/// <summary>Quest configuration input interpreted in the parent Event's zone; no independent zone or parent reassignment is accepted.</summary>
/// <param name="Title">Proposed title, 3 through 120 characters after trimming.</param>
/// <param name="Description">Plain-text activity description, at most 10000 characters after trimming.</param>
/// <param name="Location">Physical or online meeting location, 1 through 500 characters for publication after trimming.</param>
/// <param name="SuggestedCapacity">Advisory attendee count from 1 through 10000, or null; never a hard joining limit.</param>
/// <param name="StartLocal">Local start wall-clock components in the inherited Event IANA zone; DateTime.Kind does not select a zone.</param>
/// <param name="EndLocal">Local end wall-clock components; resolved instant must exceed start and fit within the Event's inclusive dates.</param>
/// <param name="StartOffset">Chosen UTC offset for an ambiguous start, or null if unambiguous; nonexistent local times are invalid.</param>
/// <param name="EndOffset">Chosen UTC offset for an ambiguous end, or null if unambiguous; any supplied offset must match the zone.</param>
/// <param name="Visibility">Member-public or named-private visibility; immutable after publication.</param>
public sealed record QuestInput(string Title, string Description, string Location, int? SuggestedCapacity,
    DateTime StartLocal, DateTime EndLocal, TimeSpan? StartOffset, TimeSpan? EndOffset, QuestVisibility Visibility);
