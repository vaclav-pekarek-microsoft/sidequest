using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Sidequest.Web.Experience;

/// <summary>Owns the connection component's module and browser bridge references for one interactive lifetime.</summary>
/// <param name="runtime">The circuit's JavaScript runtime, used only after interactive rendering.</param>
public sealed class ExperienceInterop(IJSRuntime runtime) : IAsyncDisposable
{
    internal const string ModulePath = "./Components/Experience/ConnectionStatus.razor.js";
    private IJSObjectReference? module;
    private IJSObjectReference? bridge;

    /// <summary>Attaches the browser bridge to rendered status elements and an owned callback reference.</summary>
    /// <typeparam name="T">The public JS-invokable callback target.</typeparam>
    /// <param name="root">Rendered component root, never an element ID lookup supplied by the caller.</param>
    /// <param name="callback">Caller-owned .NET callback reference.</param>
    /// <param name="cancellationToken">Cancels module loading and initialization.</param>
    /// <returns>A task completing when the bridge is listening; storage failures are shown by the bridge.</returns>
    public async ValueTask InitializeAsync<T>(ElementReference root, DotNetObjectReference<T> callback,
        CancellationToken cancellationToken) where T : class
    {
        module = await runtime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
        bridge = await module.InvokeAsync<IJSObjectReference>("initialize", cancellationToken, root, callback);
    }

    /// <summary>Requests an explicit authenticated full Joined refresh; JavaScript displays any storage or network failure.</summary>
    /// <param name="cancellationToken">Cancels the interop wait; disposing the bridge aborts its HTTP request.</param>
    /// <returns>A task completing after the refresh attempt, not a guarantee that storage is available.</returns>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        if (bridge is not null) await bridge.InvokeVoidAsync("refresh", cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (bridge is not null)
            {
                await bridge.InvokeVoidAsync("dispose");
                await bridge.DisposeAsync();
            }
            if (module is not null) await module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
