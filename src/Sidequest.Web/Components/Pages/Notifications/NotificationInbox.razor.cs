using Microsoft.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications;

namespace Sidequest.Web.Components.Pages.Notifications;

/// <summary>Recipient inbox whose list and unread count are refreshed together before reconnect reveals protected content.</summary>
public partial class NotificationInbox
{
    [Inject] private INotificationService Service { get; set; } = default!;
    private PageResult<NotificationSummary>? items;
    private int unread;
    private int page = 1;

    /// <inheritdoc />
    protected override async Task QueryAsync(CancellationToken token)
    {
        items = null;
        unread = 0;
        var loaded = await Service.ListAsync(new(page), token);
        token.ThrowIfCancellationRequested();
        var count = await Service.UnreadCountAsync(token);
        token.ThrowIfCancellationRequested();
        items = loaded;
        unread = count;
    }

    private Task MarkAsync(Guid? id)
    {
        if (id is null ? unread == 0 : items?.Items.Any(item => item.Id == id && !item.IsRead) != true)
            return Task.CompletedTask;
        return MutateAsync(async token =>
        {
            items = null;
            unread = 0;
            await Service.MarkReadAsync(id, token);
            token.ThrowIfCancellationRequested();
            Status = "Read state saved.";
            await QueryAsync(token);
        });
    }

    private Task MoveAsync(int delta)
    {
        if (ControlsDisabled || (delta < 0 ? page == 1 : items is null || page * 25 >= items.TotalCount))
            return Task.CompletedTask;
        page += delta;
        return RetryAsync();
    }

    /// <inheritdoc />
    protected override void ClearProtectedState()
    {
        items = null;
        unread = 0;
    }
}
