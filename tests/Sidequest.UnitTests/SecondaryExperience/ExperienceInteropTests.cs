using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Checks typed interop ownership, cooperative cancellation forwarding and expected server-disconnect disposal.</summary>
public sealed class ExperienceInteropTests
{
    /// <summary>Imports only on explicit interactive initialization, forwards cancellation and releases both owned JavaScript references.</summary>
    /// <returns>Completion after exact operation-order and disposal assertions.</returns>
    [Fact]
    public async Task InitializationRefreshAndDisposalOwnOneModuleAndBridge()
    {
        var calls = new List<string>();
        var bridge = new RecordingReference("bridge", calls);
        var module = new RecordingReference("module", calls) { Result = bridge };
        var runtime = new RecordingRuntime(module, calls);
        var interop = new ExperienceInterop(runtime);
        Assert.Empty(calls);
        using var callback = DotNetObjectReference.Create(new object());
        using var cancellation = new CancellationTokenSource();
        await interop.InitializeAsync(default(ElementReference), callback, "transient-circuit-proof", cancellation.Token);
        await interop.RefreshAsync(cancellation.Token);
        await interop.DisposeAsync();
        Assert.Equal(new[] { "import", "module.initialize", "bridge.refresh", "bridge.dispose", "bridge.release", "module.release" }, calls);
        Assert.Equal(cancellation.Token, runtime.LastToken);
        Assert.Equal(cancellation.Token, module.LastToken);
        Assert.Equal(new object?[] { default(ElementReference), callback, "transient-circuit-proof" }, module.LastArguments);
        Assert.Equal(2, bridge.InvocationCount);
        Assert.True(module.Released);
        Assert.True(bridge.Released);
    }

    /// <summary>A circuit lost while releasing browser listeners is an expected lifecycle outcome, not a swallowed arbitrary JavaScript fault.</summary>
    /// <returns>Completion after disconnect-specific disposal and unexpected-failure assertions.</returns>
    [Fact]
    public async Task DisposalToleratesOnlyExpectedCircuitDisconnection()
    {
        var calls = new List<string>();
        var bridge = new RecordingReference("bridge", calls);
        var module = new RecordingReference("module", calls) { Result = bridge };
        var interop = new ExperienceInterop(new RecordingRuntime(module, calls));
        using var callback = DotNetObjectReference.Create(new object());
        await interop.InitializeAsync(default(ElementReference), callback, null, CancellationToken.None);
        bridge.Failure = new JSDisconnectedException("Synthetic circuit disconnect");
        await interop.DisposeAsync();
        bridge.Failure = new JSException("Synthetic unexpected JavaScript failure");
        await Assert.ThrowsAsync<JSException>(async () => await interop.DisposeAsync());
    }

    private sealed class RecordingRuntime(IJSObjectReference module, List<string> calls) : IJSRuntime
    {
        internal CancellationToken LastToken { get; private set; }
        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            calls.Add(identifier);
            LastToken = cancellationToken;
            Assert.Equal("./Components/Experience/ConnectionStatus.razor.js", Assert.Single(args!));
            return ValueTask.FromResult((TValue)module);
        }
    }

    private sealed class RecordingReference(string name, List<string> calls) : IJSObjectReference
    {
        internal object? Result { get; init; }
        internal object?[]? LastArguments { get; private set; }
        internal CancellationToken LastToken { get; private set; }
        internal Exception? Failure { get; set; }
        internal bool Released { get; private set; }
        internal int InvocationCount { get; private set; }
        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        /// <inheritdoc />
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            calls.Add($"{name}.{identifier}");
            LastToken = cancellationToken;
            LastArguments = args;
            InvocationCount++;
            return Failure is null ? ValueTask.FromResult(Result is TValue value ? value : default!) :
                ValueTask.FromException<TValue>(Failure);
        }
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Released = true;
            calls.Add($"{name}.release");
            return ValueTask.CompletedTask;
        }
    }
}
