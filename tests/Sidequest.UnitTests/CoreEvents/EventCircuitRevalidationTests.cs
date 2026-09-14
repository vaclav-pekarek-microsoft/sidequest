using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Components.Events;

namespace Sidequest.UnitTests.CoreEvents;

/// <summary>Verifies awaited reconnect reauthorization, subscription removal, and isolation between user circuit scopes.</summary>
public sealed class EventCircuitRevalidationTests
{
    /// <summary>Awaits each renderer callback in order and stops calling a component after it unsubscribes.</summary>
    /// <returns>A task completing after controlled callback release and unsubscribe assertions.</returns>
    [Fact]
    public async Task ReconnectAwaitsCallbacksAndHonorsUnsubscribe()
    {
        var service = new EventCircuitRevalidation();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new List<string>();
        async Task FirstAsync()
        {
            steps.Add("first-start");
            await gate.Task;
            steps.Add("first-end");
        }
        Task SecondAsync()
        {
            steps.Add("second");
            return Task.CompletedTask;
        }
        service.Reconnected += FirstAsync;
        service.Reconnected += SecondAsync;
        // Circuit is framework-owned and is not inspected by this scope-local notification handler.
        var reconnect = service.OnConnectionUpAsync(null!, default);
        Assert.Equal(new[] { "first-start" }, steps);
        Assert.False(reconnect.IsCompleted);
        gate.SetResult();
        await reconnect;
        Assert.Equal(new[] { "first-start", "first-end", "second" }, steps);
        service.Reconnected -= SecondAsync;
        steps.Clear();
        await service.OnConnectionUpAsync(null!, default);
        Assert.Equal(new[] { "first-start", "first-end" }, steps);
    }

    /// <summary>Uses the same notifier for a scope's CircuitHandler while keeping different users' subscriptions isolated.</summary>
    /// <returns>A task completing after real DI scopes and separate reconnect callbacks are exercised.</returns>
    [Fact]
    public async Task RegistrationsShareOnlyWithinOneCircuitScope()
    {
        var services = new ServiceCollection();
        services.AddEventUiRevalidation();
        await using var provider = services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var firstNotifier = first.ServiceProvider.GetRequiredService<EventCircuitRevalidation>();
        var secondNotifier = second.ServiceProvider.GetRequiredService<EventCircuitRevalidation>();
        var firstHandler = Assert.Single(first.ServiceProvider.GetServices<CircuitHandler>());
        Assert.Same(firstNotifier, firstHandler);
        Assert.NotSame(firstNotifier, secondNotifier);
        var firstCalls = 0;
        var secondCalls = 0;
        firstNotifier.Reconnected += () => { firstCalls++; return Task.CompletedTask; };
        secondNotifier.Reconnected += () => { secondCalls++; return Task.CompletedTask; };
        await firstHandler.OnConnectionUpAsync(null!, default);
        Assert.Equal(1, firstCalls);
        Assert.Equal(0, secondCalls);
        await secondNotifier.OnConnectionUpAsync(null!, default);
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    /// <summary>Cancellation stops reconnect work before invoking another component's authorization callback.</summary>
    /// <returns>A task completing after cancellation and zero-callback assertions.</returns>
    [Fact]
    public async Task CancelledReconnectDoesNotInvokeSubscribers()
    {
        var service = new EventCircuitRevalidation();
        var calls = 0;
        service.Reconnected += () => { calls++; return Task.CompletedTask; };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.OnConnectionUpAsync(null!, cancellation.Token));
        Assert.Equal(0, calls);
    }
}
