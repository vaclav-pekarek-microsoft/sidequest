using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Notifications;

public sealed record NotificationSummary(Guid Id, NotificationKind Kind, string Summary,
    Guid? EventId, Guid? QuestId, DateTimeOffset CreatedUtc, bool IsRead);
public sealed record PreferenceInput(bool NewQuestEmail, bool ActivityEmail, bool RemindersEnabled,
    decimal ReminderHours, string? TimeZoneId);
public sealed record DeliveryFailure(Guid Id, string Kind, string Error, int Attempts, DateTimeOffset DueUtc);

public interface INotificationService
{
    Task<PageResult<NotificationSummary>> ListAsync(PageRequest page, CancellationToken cancellationToken = default);
    Task<int> UnreadCountAsync(CancellationToken cancellationToken = default);
    Task MarkReadAsync(Guid? notificationId, CancellationToken cancellationToken = default);
    Task<PreferenceInput> GetPreferencesAsync(CancellationToken cancellationToken = default);
    Task SavePreferencesAsync(PreferenceInput input, CancellationToken cancellationToken = default);
    Task SetEventNewQuestEmailAsync(Guid eventId, bool enabled, CancellationToken cancellationToken = default);
    Task<string> DownloadCalendarAsync(Guid questId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryFailure>> FailedDeliveriesAsync(CancellationToken cancellationToken = default);
    Task ReplayAsync(Guid deliveryId, string kind, CancellationToken cancellationToken = default);
}
