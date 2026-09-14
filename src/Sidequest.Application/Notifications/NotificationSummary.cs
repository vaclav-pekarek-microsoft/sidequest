using Sidequest.Domain.Model;

namespace Sidequest.Application.Notifications;

/// <summary>Current recipient's in-app projection, filtered so retained items cannot disclose protected content after access loss.</summary>
/// <param name="Id">Internal notification identifier.</param>
/// <param name="Kind">Business trigger determining notification policy.</param>
/// <param name="Summary">Safe current display text; access-loss notices must use minimal historical identifiers.</param>
/// <param name="EventId">Related Event identifier, or null when no Event reference is supplied.</param>
/// <param name="QuestId">Related Quest identifier, or null when no Quest reference is supplied; possession grants no resource access.</param>
/// <param name="CreatedUtc">UTC notification creation instant.</param>
/// <param name="IsRead">Whether the current recipient has marked the item read.</param>
public sealed record NotificationSummary(Guid Id, NotificationKind Kind, string Summary,
    Guid? EventId, Guid? QuestId, DateTimeOffset CreatedUtc, bool IsRead);
