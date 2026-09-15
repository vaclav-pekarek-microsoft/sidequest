using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Experience;

/// <summary>Attaches browser connection/install feedback after rendering without storing a circuit principal or protected content.</summary>
public partial class ConnectionStatus : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private ElementReference root;
    private ExperienceInterop? interop;
    private DotNetObjectReference<ConnectionStatus>? callback;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ExperienceCoordinator Coordinator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;
    [Inject] private ExperienceSessionBinding SessionBinding { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized() => Coordinator.SnapshotRefreshRequested += RefreshAsync;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        var cancellationToken = lifetime.Token;
        interop = new(JS);
        callback = DotNetObjectReference.Create(this);
        var state = await AuthenticationState.GetAuthenticationStateAsync();
        if (cancellationToken.IsCancellationRequested) return;
        var binding = SessionBinding.Create(state.User);
        try { await interop.InitializeAsync(root, callback, binding, cancellationToken); }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>Receives an untrusted browser connectivity hint and zone; resources are independently reauthorized.</summary>
    /// <param name="connected">True only when the bridge reports both network and circuit connectivity.</param>
    /// <param name="zone">Actual browser zone; null means use the labeled Event-zone fallback.</param>
    /// <returns>A task completing after renderer-safe subscribed updates and fresh authorization checks.</returns>
    /// <remarks>Every connected transition forces reauthorization because a disconnected browser cannot reliably deliver its preceding down notification.</remarks>
    [JSInvokable]
    public Task ConnectionChangedAsync(bool connected, string? zone) =>
        InvokeAsync(async () =>
        {
            if (connected) await Coordinator.ReportConnectionAsync(false, zone);
            await Coordinator.ReportConnectionAsync(connected, zone);
        });

    private async Task RefreshAsync()
    {
        if (interop is null || !Coordinator.CanUseOnlineActions) return;
        try { await interop.RefreshAsync(lifetime.Token); }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Coordinator.SnapshotRefreshRequested -= RefreshAsync;
        await lifetime.CancelAsync();
        if (interop is not null) await interop.DisposeAsync();
        callback?.Dispose();
        lifetime.Dispose();
    }
}
