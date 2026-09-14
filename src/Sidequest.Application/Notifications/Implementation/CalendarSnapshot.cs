namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Immutable single-recipient calendar input retained across retries; cancellation contains no protected descriptive text.</summary>
/// <param name="QuestId">Quest identity used to derive its stable globally unique UID.</param>
/// <param name="Sequence">Transactionally allocated monotonically increasing calendar revision.</param>
/// <param name="StampUtc">Original change timestamp, never refreshed by a retry.</param>
/// <param name="StartUtc">Original UTC start.</param>
/// <param name="EndUtc">Original UTC exclusive end.</param>
/// <param name="Recipient">Trusted directory recipient address.</param>
/// <param name="Method">REQUEST or CANCEL.</param>
/// <param name="Title">Authorized title; generic on withdrawal.</param>
/// <param name="Description">Authorized plain text; empty on withdrawal.</param>
/// <param name="Location">Authorized location; empty on withdrawal.</param>
public sealed record CalendarSnapshot(Guid QuestId, long Sequence, DateTimeOffset StampUtc,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Recipient, string Method,
    string Title, string Description, string Location);
