using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.CoreDelivery;

internal sealed class NotificationUiService : INotificationService
{
    internal TaskCompletionSource<PageResult<NotificationSummary>>? Inbox { get; set; }
    internal TaskCompletionSource<PreferenceInput>? Preferences { get; set; }
    internal TaskCompletionSource<IReadOnlyList<DeliveryFailure>>? FailureQuery { get; set; }
    internal TaskCompletionSource<int>? UnreadQuery { get; set; }
    internal TaskCompletionSource? Mutation { get; set; }
    internal bool Denied { get; set; }
    internal Exception? ReadFailure { get; set; }
    internal PageResult<NotificationSummary> Items { get; set; } = new([], 0, 1, 25);
    internal int Unread { get; set; }
    internal int ListCalls { get; private set; }
    internal int CountCalls { get; private set; }
    internal int PreferenceCalls { get; private set; }
    internal int FailureCalls { get; private set; }
    internal int SaveCalls { get; private set; }
    internal List<Guid?> Marks { get; } = [];
    internal List<(Guid Id, bool Enabled)> EventOverrides { get; } = [];
    internal List<CancellationToken> Tokens { get; } = [];
    internal PreferenceInput? Saved { get; private set; }
    internal List<(Guid Id, string Kind)> Replays { get; } = [];
    internal IReadOnlyList<DeliveryFailure> Failures { get; set; } = [];

    /// <inheritdoc/>
    public Task<PageResult<NotificationSummary>> ListAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ListCalls++;
        Tokens.Add(cancellationToken);
        return Read(Inbox?.Task ?? Task.FromResult(Items));
    }
    /// <inheritdoc/>
    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default)
    {
        CountCalls++;
        Tokens.Add(cancellationToken);
        return Read(UnreadQuery?.Task ?? Task.FromResult(Unread));
    }
    /// <inheritdoc/>
    public Task MarkReadAsync(Guid? notificationId, CancellationToken cancellationToken = default)
    {
        Marks.Add(notificationId);
        Tokens.Add(cancellationToken);
        return Mutation?.Task ?? Task.CompletedTask;
    }
    /// <inheritdoc/>
    public Task<PreferenceInput> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        PreferenceCalls++;
        Tokens.Add(cancellationToken);
        return Read(Preferences?.Task ?? Task.FromResult(new PreferenceInput(false, true, true, 1, null)));
    }
    /// <inheritdoc/>
    public Task SavePreferencesAsync(PreferenceInput input, CancellationToken cancellationToken = default)
    {
        NotificationRules.Validate(input);
        SaveCalls++;
        Tokens.Add(cancellationToken);
        Saved = input;
        return Mutation?.Task ?? Task.CompletedTask;
    }
    /// <inheritdoc/>
    public Task SetEventNewQuestEmailAsync(Guid eventId, bool enabled, CancellationToken cancellationToken = default)
    {
        EventOverrides.Add((eventId, enabled));
        Tokens.Add(cancellationToken);
        return Mutation?.Task ?? Task.CompletedTask;
    }
    /// <inheritdoc/>
    public Task<string> DownloadCalendarAsync(Guid questId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <inheritdoc/>
    public Task<IReadOnlyList<DeliveryFailure>> FailedDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        FailureCalls++;
        Tokens.Add(cancellationToken);
        return Read(FailureQuery?.Task ?? Task.FromResult(Failures));
    }
    /// <inheritdoc/>
    public Task ReplayAsync(Guid deliveryId, string kind, CancellationToken cancellationToken = default)
    {
        Replays.Add((deliveryId, kind));
        Tokens.Add(cancellationToken);
        return Mutation?.Task ?? Task.CompletedTask;
    }

    private Task<T> Read<T>(Task<T> result) => ReadFailure is { } failure
        ? Task.FromException<T>(failure)
        : Denied ? Task.FromException<T>(new DomainException(ErrorCode.Forbidden, "private-secret")) : result;
}
