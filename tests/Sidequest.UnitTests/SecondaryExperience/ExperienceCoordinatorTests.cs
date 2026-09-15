using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Verifies deterministic, awaited per-circuit connection and refresh coordination without treating it as authorization.</summary>
public sealed class ExperienceCoordinatorTests
{
    /// <summary>Initial and recovered connections reauthorize once each; duplicate connected reports never retry mutations.</summary>
    /// <returns>Completion after ordering, callback count, and zone assertions.</returns>
    [Fact]
    public async Task ReconnectAwaitsReauthorizationAndFullSnapshotRefresh()
    {
        var coordinator = new ExperienceCoordinator();
        var calls = new List<string>();
        coordinator.Changed += () => { calls.Add("changed"); return Task.CompletedTask; };
        coordinator.ReauthorizationRequested += () => { calls.Add("authorize"); return Task.CompletedTask; };
        coordinator.SnapshotRefreshRequested += () => { calls.Add("refresh"); return Task.CompletedTask; };
        Assert.False(coordinator.CanUseOnlineActions);
        await coordinator.ReportConnectionAsync(true, "Europe/Prague");
        Assert.Equal(new[] { "changed", "authorize", "refresh" }, calls);
        calls.Clear();
        await coordinator.ReportConnectionAsync(true, "Europe/Prague");
        Assert.Equal(new[] { "changed" }, calls);
        calls.Clear();
        await coordinator.ReportConnectionAsync(false, null);
        Assert.False(coordinator.CanUseOnlineActions);
        Assert.Null(coordinator.BrowserTimeZone);
        await coordinator.ReportConnectionAsync(true, "America/New_York");
        Assert.True(coordinator.CanUseOnlineActions);
        Assert.Equal("America/New_York", coordinator.BrowserTimeZone);
        Assert.Equal(new[] { "changed", "changed", "authorize", "refresh" }, calls);
    }

    /// <summary>Refresh subscribers run in order and a failure is surfaced, not replaced with a success-shaped cache result.</summary>
    /// <returns>Completion after exception identity and later-subscriber suppression assertions.</returns>
    [Fact]
    public async Task RefreshFailuresAreAwaitedAndNotSilentlyIgnored()
    {
        var coordinator = new ExperienceCoordinator();
        var failure = new InvalidOperationException("Synthetic storage failure");
        var subsequent = false;
        coordinator.SnapshotRefreshRequested += () => Task.FromException(failure);
        coordinator.SnapshotRefreshRequested += () => { subsequent = true; return Task.CompletedTask; };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(coordinator.RequestSnapshotRefreshAsync));
        Assert.False(subsequent);
    }
}
