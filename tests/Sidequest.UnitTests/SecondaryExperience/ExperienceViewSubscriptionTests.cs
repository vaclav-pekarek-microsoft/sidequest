using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Verifies deferred view refreshes and awaited, disposable per-circuit subscriptions.</summary>
public sealed class ExperienceViewSubscriptionTests
{
    /// <summary>Lifecycle work defers interop, while a mutation after rendering awaits its snapshot attempt before completing.</summary>
    /// <returns>A task completing after the initial render flush and a separately blocked mutation refresh.</returns>
    [Fact]
    public async Task CompletedOperation_DefersBeforeRenderAndAwaitsRefreshAfterRender()
    {
        var coordinator = new ExperienceCoordinator();
        await coordinator.ReportConnectionAsync(true, null);
        var refreshes = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SnapshotRefreshRequested += () =>
        {
            refreshes++;
            return refreshes == 1 ? Task.CompletedTask : release.Task;
        };
        await using var view = new ExperienceViewSubscription(coordinator, () => Task.CompletedTask);
        await view.AfterOperationAsync();
        Assert.Equal(0, refreshes);
        await view.AfterRenderAsync();
        Assert.Equal(1, refreshes);
        var mutation = view.AfterOperationAsync();
        Assert.Equal(2, refreshes);
        Assert.False(mutation.IsCompleted);
        release.SetResult();
        await mutation;
    }

    /// <summary>Multiple marks produce one refresh only after an online render, never during the marking call.</summary>
    /// <returns>A task completing after exact refresh counts and disposed-view rejection.</returns>
    [Fact]
    public async Task PendingRefresh_IsDeferredCoalescedAndDiscardedOnDisposal()
    {
        var coordinator = new ExperienceCoordinator();
        await coordinator.ReportConnectionAsync(true, null);
        var refreshes = 0;
        coordinator.SnapshotRefreshRequested += () => { refreshes++; return Task.CompletedTask; };
        await using var view = new ExperienceViewSubscription(coordinator, () => Task.CompletedTask);
        view.RequestSnapshotRefresh();
        view.RequestSnapshotRefresh();
        Assert.Equal(0, refreshes);
        await view.AfterRenderAsync();
        Assert.Equal(1, refreshes);
        await view.AfterRenderAsync();
        Assert.Equal(1, refreshes);
        await coordinator.ReportConnectionAsync(false, null);
        view.RequestSnapshotRefresh();
        await view.AfterRenderAsync();
        Assert.Equal(1, refreshes);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(2, refreshes);
        await view.AfterRenderAsync();
        Assert.Equal(3, refreshes);
        view.RequestSnapshotRefresh();
        await view.DisposeAsync();
        view.RequestSnapshotRefresh();
        await view.AfterRenderAsync();
        Assert.Equal(3, refreshes);
    }

    /// <summary>Reconnection cannot complete before view reauthorization; disposal removes both subscribed delegates.</summary>
    /// <returns>A task completing after the blocked callback, subsequent notification and disposed-circuit transitions.</returns>
    [Fact]
    public async Task Reauthorization_IsAwaitedAndDisposedCallbacksDoNotRun()
    {
        var coordinator = new ExperienceCoordinator();
        var changed = 0;
        var checks = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var view = new ExperienceViewSubscription(coordinator,
            () => { changed++; return Task.CompletedTask; },
            () => { checks++; return ready.Task; });
        var reconnect = coordinator.ReportConnectionAsync(true, "Europe/Prague");
        Assert.Equal(1, changed);
        Assert.Equal(1, checks);
        Assert.False(reconnect.IsCompleted);
        ready.SetResult();
        await reconnect;
        await view.DisposeAsync();
        await coordinator.ReportConnectionAsync(false, null);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(1, changed);
        Assert.Equal(1, checks);
    }
}
