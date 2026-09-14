using Sidequest.Application.Abstractions;

namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Version-one durable per-recipient intent; content is authorized again before transport submission.</summary>
/// <param name="Version">Supported payload version, currently one.</param>
/// <param name="Change">Captured source intent, never a grant of current access.</param>
/// <param name="RecipientId">Sole recipient identity.</param>
/// <param name="Mandatory">Whether optional email preferences are ignored for service communication.</param>
/// <param name="Calendar">Immutable calendar snapshot, or null for ordinary mail.</param>
/// <param name="CalendarContent">Exact rendered retry bytes as text, or null until safe rendering succeeds.</param>
public sealed record DeliveryPayload(int Version, ChangeEnvelope Change, Guid RecipientId,
    bool Mandatory, CalendarSnapshot? Calendar = null, string? CalendarContent = null);
