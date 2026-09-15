using Microsoft.AspNetCore.Components;
using Sidequest.Domain.Rules;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Notifications;

/// <summary>Coordinates notification view authorization, serial operations and circuit lifetime without retrying user mutations.</summary>
public abstract class NotificationViewBase : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private ExperienceViewSubscription? subscription;
    private Task pendingOperation = Task.CompletedTask;
    private bool disposed;
    private bool verified;
    private int authorizationVersion;
    private int completedAuthorizationVersion = -1;
    private bool parametersSet;
    private Guid? contextId;

    [Inject] private ExperienceCoordinator Experience { get; set; } = default!;
    [Inject] private ILogger<NotificationViewBase> Logger { get; set; } = default!;

    /// <summary>Whether an application operation is still running; reconnect waits for it rather than dropping its authorization check.</summary>
    protected bool Busy { get; private set; }

    /// <summary>Disables every editing control until the interactive, online view is idle and currently authorized.</summary>
    protected bool ControlsDisabled => RefreshDisabled || !verified;

    /// <summary>Allows an explicit read-only retry of a failed authorization check, but never an offline or overlapping request.</summary>
    protected bool RefreshDisabled => disposed || Busy || !RendererInfo.IsInteractive || !Experience.CanUseOnlineActions;

    /// <summary>Whether protected data or editable drafts may be rendered; retained unverified drafts remain hidden.</summary>
    protected bool CanDisplayProtectedState => verified && (!RendererInfo.IsInteractive || Experience.CanUseOnlineActions);

    /// <summary>Safe user-facing failure text; unexpected provider diagnostics are logged only on the server.</summary>
    protected string? Error { get; private set; }

    /// <summary>Outcome of the last explicit user action, cleared before reconnect authorization.</summary>
    protected string Status { get; set; } = "";

    /// <summary>Optional resource identity whose navigation change invalidates retained drafts and requires a new access check.</summary>
    protected virtual Guid? ContextId => null;

    /// <inheritdoc />
    protected override void OnInitialized() =>
        subscription = new(Experience, ConnectionChangedAsync, ReauthorizeAsync);

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        if (parametersSet && contextId == ContextId)
            return Task.CompletedTask;
        parametersSet = true;
        contextId = ContextId;
        return CheckCurrentAccessAsync(clearState: true);
    }

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender) =>
        RendererInfo.IsInteractive ? subscription?.AfterRenderAsync() ?? Task.CompletedTask : Task.CompletedTask;

    private Task ConnectionChangedAsync() => InvokeAsync(() =>
    {
        if (disposed)
            return;
        if (!Experience.CanUseOnlineActions)
        {
            authorizationVersion++;
            verified = false;
        }
        StateHasChanged();
    });

    private Task ReauthorizeAsync() => InvokeAsync(async () =>
    {
        await CheckCurrentAccessAsync(clearState: false);
        if (!disposed)
            StateHasChanged();
    });

    private async Task CheckCurrentAccessAsync(bool clearState)
    {
        if (disposed)
            return;
        verified = false;
        authorizationVersion++;
        // A later connection invalidates earlier reads; only read-only authorization may repeat.
        do
        {
            while (Busy)
                await pendingOperation;
            if (disposed)
                return;
            if (clearState)
            {
                ClearProtectedState();
                clearState = false;
            }
            else if (completedAuthorizationVersion == authorizationVersion)
            {
                return;
            }
            var version = authorizationVersion;
            await CheckAccessAsync();
            if (version == authorizationVersion)
                return;
        }
        while (!disposed && Experience.CanUseOnlineActions);
    }

    /// <summary>Retries current read-only authorization without replacing an unsaved draft or replaying a mutation.</summary>
    /// <returns>The authorized refresh, or immediate completion when the view cannot start a request.</returns>
    protected Task RetryAsync() => RefreshDisabled ? Task.CompletedTask : CheckAccessAsync();

    private Task CheckAccessAsync()
    {
        verified = false;
        Status = "";
        return RunAsync(QueryAsync, authorizationCheck: true);
    }

    /// <summary>Queries current server-owned access and display state, preserving drafts until confirmed access loss.</summary>
    /// <param name="token">Stable lifetime token; check it after each await before assigning state or starting dependent work.</param>
    /// <returns>Completion after all required authorization queries and protected projections are current.</returns>
    protected abstract Task QueryAsync(CancellationToken token);

    /// <summary>Deletes protected projections and unsaved input after confirmed Forbidden or NotFound outcomes.</summary>
    protected abstract void ClearProtectedState();

    /// <summary>Executes one immediate mutation only when controls are enabled; disabled callbacks are discarded, never queued.</summary>
    /// <param name="action">Authorized application work, including any dependent display refresh.</param>
    /// <returns>The operation completion, or a completed task when disabled.</returns>
    protected Task MutateAsync(Func<CancellationToken, Task> action) =>
        ControlsDisabled ? Task.CompletedTask : RunAsync(action, authorizationCheck: false);

    private async Task RunAsync(Func<CancellationToken, Task> action, bool authorizationCheck)
    {
        if (disposed)
            return;
        var token = lifetime.Token;
        var version = authorizationVersion;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOperation = completed.Task;
        Busy = true;
        Error = null;
        Status = "";
        try
        {
            await action(token);
            token.ThrowIfCancellationRequested();
            if (authorizationCheck && version == authorizationVersion)
                verified = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disposal owns this cancellation, including late non-cooperative query results.
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // A non-cooperative service may fault after disposal; it no longer owns display state.
        }
        catch (DomainException failure) when (!token.IsCancellationRequested)
        {
            if (failure.Code is ErrorCode.Forbidden or ErrorCode.NotFound)
            {
                verified = false;
                ClearProtectedState();
                Status = "";
                Error = "This notification view is unavailable or your session no longer has access.";
            }
            else
            {
                Error = failure.Code == ErrorCode.Validation && !authorizationCheck
                    ? failure.Message
                    : "The operation could not be completed. Check current access and retry.";
            }
        }
        catch (Exception failure) when (!token.IsCancellationRequested)
        {
            verified = false;
            Logger.LogError(failure, "Notification view operation failed in {Component}.", GetType().Name);
            Error = "Current access could not be verified. Your unsaved input is kept hidden; retry to check access.";
        }
        finally
        {
            if (authorizationCheck && !disposed && version == authorizationVersion)
                completedAuthorizationVersion = version;
            Busy = false;
            completed.TrySetResult();
        }
        if (!token.IsCancellationRequested && subscription is not null)
            await subscription.AfterOperationAsync();
    }

    /// <summary>Unsubscribes once, clears retained sensitive state and cancels outstanding work without waiting for non-cooperative services.</summary>
    /// <returns>Completion after owned cancellation and subscription resources have been released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        verified = false;
        authorizationVersion++;
        if (subscription is not null)
            await subscription.DisposeAsync();
        await lifetime.CancelAsync();
        lifetime.Dispose();
        ClearProtectedState();
        GC.SuppressFinalize(this);
    }
}
