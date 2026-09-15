namespace Sidequest.Web.Experience;

/// <summary>Owns one view's circuit subscriptions and defers snapshot interop until an interactive render.</summary>
internal sealed class ExperienceViewSubscription : IAsyncDisposable
{
    private readonly ExperienceCoordinator coordinator;
    private readonly Func<Task> changed;
    private readonly Func<Task>? reauthorize;
    private bool refreshPending;
    private bool disposed;
    private bool rendered;

    internal ExperienceViewSubscription(ExperienceCoordinator coordinator, Func<Task> changed, Func<Task>? reauthorize = null)
    {
        this.coordinator = coordinator;
        this.changed = changed;
        this.reauthorize = reauthorize;
        coordinator.Changed += OnChangedAsync;
        if (reauthorize is not null)
            coordinator.ReauthorizationRequested += OnReauthorizeAsync;
    }

    internal void RequestSnapshotRefresh() => refreshPending = !disposed;

    internal Task AfterOperationAsync()
    {
        RequestSnapshotRefresh();
        return rendered ? FlushAsync() : Task.CompletedTask;
    }

    internal Task AfterRenderAsync()
    {
        rendered = true;
        return FlushAsync();
    }

    private async Task FlushAsync()
    {
        if (disposed || !refreshPending || !coordinator.CanUseOnlineActions)
            return;
        refreshPending = false;
        await coordinator.RequestSnapshotRefreshAsync();
    }

    private Task OnChangedAsync() => disposed ? Task.CompletedTask : changed();
    private Task OnReauthorizeAsync() => disposed ? Task.CompletedTask : reauthorize!();

    /// <summary>Stops callbacks into a removed view without disposing its shared per-circuit coordinator.</summary>
    /// <returns>A completed disposal operation; no background work or resources are retained.</returns>
    public ValueTask DisposeAsync()
    {
        disposed = true;
        refreshPending = false;
        coordinator.Changed -= OnChangedAsync;
        if (reauthorize is not null)
            coordinator.ReauthorizationRequested -= OnReauthorizeAsync;
        return ValueTask.CompletedTask;
    }
}
