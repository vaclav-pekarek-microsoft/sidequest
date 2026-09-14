using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.CoreDelivery;

internal sealed class NotificationUiService : INotificationService
{
    internal TaskCompletionSource<PageResult<NotificationSummary>>? Inbox { get; set; }
    internal bool Denied { get; set; }
    internal PreferenceInput? Saved { get; private set; }
    internal List<(Guid Id, string Kind)> Replays { get; } = [];
    internal IReadOnlyList<DeliveryFailure> Failures { get; set; } = [];

    /// <inheritdoc/>
    public Task<PageResult<NotificationSummary>> ListAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        Denied ? Task.FromException<PageResult<NotificationSummary>>(new DomainException(ErrorCode.Forbidden, "private-secret")) :
        Inbox?.Task ?? Task.FromResult(new PageResult<NotificationSummary>([], 0, page.Page, page.PageSize));
    /// <inheritdoc/>
    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    /// <inheritdoc/>
    public Task MarkReadAsync(Guid? notificationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task<PreferenceInput> GetPreferencesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PreferenceInput(false, true, true, 1, null));
    /// <inheritdoc/>
    public Task SavePreferencesAsync(PreferenceInput input, CancellationToken cancellationToken = default)
    {
        NotificationRules.Validate(input);
        Saved = input;
        return Task.CompletedTask;
    }
    /// <inheritdoc/>
    public Task SetEventNewQuestEmailAsync(Guid eventId, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task<string> DownloadCalendarAsync(Guid questId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <inheritdoc/>
    public Task<IReadOnlyList<DeliveryFailure>> FailedDeliveriesAsync(CancellationToken cancellationToken = default) =>
        Denied ? Task.FromException<IReadOnlyList<DeliveryFailure>>(new DomainException(ErrorCode.Forbidden, "private-secret")) : Task.FromResult(Failures);
    /// <inheritdoc/>
    public Task ReplayAsync(Guid deliveryId, string kind, CancellationToken cancellationToken = default)
    {
        Replays.Add((deliveryId, kind));
        return Task.CompletedTask;
    }
}
