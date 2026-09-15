namespace Sidequest.Web.Experience;

/// <summary>Per-circuit coordination only; browser connectivity is a UX hint and never replaces resource authorization.</summary>
public sealed class ExperienceCoordinator
{
    /// <summary>Whether the browser last reported a connected circuit and network; false until the interactive bridge is ready.</summary>
    public bool CanUseOnlineActions { get; private set; }
    /// <summary>The browser's actual IANA time zone, or null when unavailable; never an editable Quest zone.</summary>
    public string? BrowserTimeZone { get; private set; }
    /// <summary>Awaited callbacks for connection/display updates; subscribers must unsubscribe on disposal.</summary>
    public event Func<Task>? Changed;
    /// <summary>Awaited callbacks that reload visible protected resources after reconnection.</summary>
    public event Func<Task>? ReauthorizationRequested;
    /// <summary>Awaited callbacks asking the browser to refresh from the cookie-authorized full Joined HTTP query.</summary>
    public event Func<Task>? SnapshotRefreshRequested;

    /// <summary>Updates circuit UX and reauthorizes on a false-to-true connection transition.</summary>
    /// <param name="connected">The browser's combined network and circuit hint.</param>
    /// <param name="browserTimeZone">Actual detected device zone, or null when detection failed.</param>
    /// <returns>A task completing after subscribed UI and reauthorization work, without retrying mutations.</returns>
    public async Task ReportConnectionAsync(bool connected, string? browserTimeZone)
    {
        var reconnecting = connected && !CanUseOnlineActions;
        CanUseOnlineActions = connected;
        BrowserTimeZone = browserTimeZone;
        await NotifyAsync(Changed);
        if (reconnecting)
        {
            await NotifyAsync(ReauthorizationRequested);
            await RequestSnapshotRefreshAsync();
        }
    }

    /// <summary>Call after a successful joined load, mutation, or subsequent resource authorization check.</summary>
    /// <returns>A task completing after refresh subscribers finish; no circuit principal or Quest data is sent to storage.</returns>
    public Task RequestSnapshotRefreshAsync() => NotifyAsync(SnapshotRefreshRequested);

    private static async Task NotifyAsync(Func<Task>? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
            await handler();
    }
}
