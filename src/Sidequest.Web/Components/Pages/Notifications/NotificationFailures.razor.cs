using Microsoft.AspNetCore.Components;
using Sidequest.Application.Notifications;

namespace Sidequest.Web.Components.Pages.Notifications;

/// <summary>Redacted administrator failure view and explicit replay confirmation backed only by application services.</summary>
public partial class NotificationFailures : IAsyncDisposable
{
    [Inject] private INotificationService Service { get; set; } = default!;
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<DeliveryFailure>? failures;
    private DeliveryFailure? pending;
    private bool busy;
    private string? error;
    private string status = "";

    /// <inheritdoc/>
    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        if (busy)
            return;
        busy = true;
        failures = null;
        pending = null;
        error = null;
        try { failures = await Service.FailedDeliveriesAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { error = "Delivery recovery is unavailable. A current administrator assignment is required."; }
        finally { busy = false; }
    }

    private async Task ReplayAsync()
    {
        if (busy || pending is null)
            return;
        var selected = pending;
        pending = null;
        failures = null;
        busy = true;
        try
        {
            await Service.ReplayAsync(selected.Id, selected.Kind, lifetime.Token);
            status = "Replay was queued, not delivered. Current eligibility and ordering will be checked.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { status = "Replay was not queued. Refresh to check authorization and current work state."; }
        finally { busy = false; }
        await LoadAsync();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
