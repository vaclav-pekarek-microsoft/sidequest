using Microsoft.AspNetCore.Components;
using Sidequest.Domain.Rules;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Administration;

/// <summary>Owns cancellation and safe operation feedback shared by focused administration pages.</summary>
public abstract class AdminPageBase : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private ExperienceViewSubscription? experience;
    private Task pendingOperation = Task.CompletedTask;
    private Task? disposal;
    private bool disposed;
    private bool reauthorizationRequired;
    private int connectionVersion;

    [Inject] private ExperienceCoordinator Experience { get; set; } = default!;
    [Inject] private ILogger<AdminPageBase> Logger { get; set; } = default!;

    /// <summary>Captures the lifetime token before its source can be disposed by navigation.</summary>
    protected AdminPageBase() => lifetimeToken = lifetime.Token;

    /// <summary>Current operation state; all interactive controls also gate on RendererInfo.IsInteractive.</summary>
    protected bool Busy { get; private set; }

    /// <summary>Safe current error including explicit conflict guidance without discarding unsaved text.</summary>
    protected string? Error { get; private set; }

    /// <summary>Whether protected content is withheld after access denial or an unverified operation failure.</summary>
    protected bool Forbidden { get; private set; }

    /// <summary>Last successful operation outcome for accessible feedback.</summary>
    protected string Status { get; set; } = "";

    /// <summary>Cancellation token tied to the component's lifetime.</summary>
    protected CancellationToken Lifetime => lifetimeToken;

    /// <summary>Rejects duplicate, offline, prerendered and reconnect-pending actions instead of queuing them.</summary>
    protected bool Disabled => disposed || Busy || reauthorizationRequired ||
        !RendererInfo.IsInteractive || !Experience.CanUseOnlineActions;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        reauthorizationRequired = !Experience.CanUseOnlineActions;
        experience = new(Experience, ConnectionChangedAsync, ReauthorizeAsync);
    }

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender) =>
        RendererInfo.IsInteractive ? experience?.AfterRenderAsync() ?? Task.CompletedTask : Task.CompletedTask;

    /// <summary>Loads the initial authorized projection during prerendering or circuit attachment, never a mutation.</summary>
    /// <param name="read">The page's initial persisted-access query.</param>
    /// <returns>The handled initial read.</returns>
    protected Task InitializeAsync(Func<Task> read) => ExecuteAsync(read);

    /// <summary>Rechecks persisted administrator access and refreshes projections without adopting draft concurrency versions.</summary>
    /// <returns>The awaited authorization and projection refresh, with no mutations or retries.</returns>
    protected abstract Task RefreshAfterReconnectAsync();

    private Task ConnectionChangedAsync() => InvokeAsync(() =>
    {
        if (disposed) return;
        if (!Experience.CanUseOnlineActions)
        {
            connectionVersion++;
            reauthorizationRequired = true;
        }
        StateHasChanged();
    });

    private Task ReauthorizeAsync() => InvokeAsync(async () =>
    {
        if (disposed) return;
        reauthorizationRequired = true;
        while (!disposed)
        {
            while (!pendingOperation.IsCompleted)
                await pendingOperation;
            if (disposed || !Experience.CanUseOnlineActions) return;
            var version = connectionVersion;
            await ExecuteAsync(RefreshAfterReconnectAsync, clearOnFailure: true);
            if (disposed) return;
            if (version != connectionVersion) continue;
            reauthorizationRequired = false;
            StateHasChanged();
            return;
        }
    });

    /// <summary>Executes a cancellation-aware operation, retaining edits on conflict and clearing sensitive state on access loss.</summary>
    /// <param name="operation">Application-service callback; it must reauthorize each request.</param>
    /// <returns>Completion of the operation or handled safe failure.</returns>
    protected Task RunAsync(Func<Task> operation) =>
        Disabled ? Task.CompletedTask : ExecuteAsync(operation);

    /// <summary>Changes local confirmation or draft selection only while interactive, online and idle.</summary>
    /// <param name="review">A synchronous review action which is discarded, never deferred, when disabled.</param>
    protected void Review(Action review)
    {
        if (!Disabled) review();
    }

    /// <summary>Reports invalid saved configuration after a successful authorized read without discarding the administrator's repair draft.</summary>
    /// <param name="validate">Validation of the freshly loaded nonsecret configuration, not an authorization callback.</param>
    protected void ValidateLoadedConfiguration(Action validate)
    {
        try { validate(); }
        catch (DomainException exception) when (exception.Code == ErrorCode.Validation)
        {
            Error = exception.Message;
        }
    }

    private async Task ExecuteAsync(Func<Task> operation, bool clearOnFailure = false)
    {
        if (disposed || lifetimeToken.IsCancellationRequested) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOperation = completion.Task;
        Busy = true;
        Error = null;
        Status = "";
        try
        {
            await operation();
            Forbidden = false;
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (DomainException exception)
        {
            Error = exception.Message;
            if (clearOnFailure || exception.Code is ErrorCode.Forbidden or ErrorCode.NotFound)
            {
                Forbidden = true;
                ClearSensitiveState();
            }
        }
        catch (Exception exception)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            Logger.LogError(exception, "Administration operation failed. Correlation {CorrelationId}.", correlationId);
            Error = $"Administration is unavailable. No success has been confirmed; reload before retrying. Reference: {correlationId}.";
            Forbidden = true;
            ClearSensitiveState();
        }
        finally
        {
            Busy = false;
            if (disposed) ClearSensitiveState();
            completion.TrySetResult();
        }
    }

    /// <summary>Clears previously loaded sensitive state when a server operation no longer authorizes it.</summary>
    protected abstract void ClearSensitiveState();

    /// <summary>Idempotently unsubscribes, cancels and awaits pending work before clearing state and releasing the cancellation source.</summary>
    /// <returns>Shared disposal completion; does not request a render during disposal.</returns>
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        if (experience is not null) await experience.DisposeAsync();
        await lifetime.CancelAsync();
        await pendingOperation;
        ClearSensitiveState();
        Status = "";
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
