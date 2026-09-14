using Microsoft.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Components.Pages.Notifications;

/// <summary>Interactive recipient inbox; protected display state is cleared before every reauthorized service request.</summary>
public partial class NotificationInbox : IAsyncDisposable
{
    [Inject] private INotificationService Service { get; set; } = default!;
    private readonly CancellationTokenSource lifetime = new();
    private PageResult<NotificationSummary>? items;
    private int unread;
    private int page = 1;
    private bool busy;
    private string? error;
    private string status = "";

    /// <inheritdoc/>
    protected override Task OnInitializedAsync() => RefreshAsync();

    private async Task RefreshAsync()
    {
        if (busy)
            return;
        busy = true;
        items = null;
        unread = 0;
        error = null;
        try
        {
            var loaded = await Service.ListAsync(new(page), lifetime.Token);
            var count = await Service.UnreadCountAsync(lifetime.Token);
            items = loaded;
            unread = count;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (DomainException) { error = "Notifications are unavailable or your session no longer has access."; }
        catch (Exception) { error = "Notifications could not be loaded. Try again shortly."; }
        finally { busy = false; }
    }

    private async Task MarkAsync(Guid? id)
    {
        if (busy)
            return;
        items = null;
        try
        {
            busy = true;
            await Service.MarkReadAsync(id, lifetime.Token);
            status = "Read state saved.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { status = "Read state was not saved. Refresh to check access and retry."; }
        finally { busy = false; }
        await RefreshAsync();
    }

    private async Task MoveAsync(int delta)
    {
        page += delta;
        await RefreshAsync();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
