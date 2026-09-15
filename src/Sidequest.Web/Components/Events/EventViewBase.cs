using Microsoft.AspNetCore.Components;
using Sidequest.Application.Events;
using Sidequest.Domain.Rules;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Events;

/// <summary>Event UI operation lifetime, cancellation, safe failure presentation, and revoked-state clearing.</summary>
public abstract class EventViewBase : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? operation;
    private bool disposed;
    private bool operationBusy;
    private ExperienceViewSubscription? experience;

    /// <summary>Per-circuit online state and cookie-authorized Joined snapshot refresh coordination.</summary>
    [Inject]
    protected ExperienceCoordinator Experience { get; set; } = null!;

    /// <summary>Server-authorized application boundary; components never access persistence or providers directly.</summary>
    [Inject]
    protected IEventService Events { get; set; } = null!;

    /// <summary>Safe diagnostics sink for unexpected UI operation failures.</summary>
    [Inject]
    protected ILogger<EventViewBase> Logger { get; set; } = null!;

    /// <summary>The current circuit's reconnect notifications, never a singleton shared with other users.</summary>
    [Inject]
    protected EventCircuitRevalidation Revalidation { get; set; } = null!;

    /// <summary>Whether controls must wait for interactivity or an application operation; prevents inert prerender clicks and repeated mutations.</summary>
    protected bool Busy
    {
        get => operationBusy || !RendererInfo.IsInteractive || !Experience.CanUseOnlineActions;
        private set => operationBusy = value;
    }

    /// <summary>Safe user-facing error text, never a raw exception or provider response.</summary>
    protected string? Error { get; private set; }

    /// <summary>Whether the last operation denied current identity or resource access, allowing children to request parent reauthorization.</summary>
    protected bool AccessDenied { get; private set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Revalidation.Reconnected += ReconnectAsync;
        experience = new(Experience, () => InvokeAsync(StateHasChanged));
    }

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender) =>
        RendererInfo.IsInteractive ? experience?.AfterRenderAsync() ?? Task.CompletedTask : Task.CompletedTask;

    /// <summary>Reauthorizes a page after reconnect, or clears a child component's stale protected projection.</summary>
    /// <returns>A task completing after renderer-owned state is refreshed.</returns>
    protected virtual Task RefreshAfterReconnectAsync()
    {
        ClearProtectedState();
        return Task.CompletedTask;
    }

    private Task ReconnectAsync() => InvokeAsync(async () =>
    {
        if (disposed)
            return;
        await RefreshAfterReconnectAsync();
        if (!disposed)
            StateHasChanged();
    });

    /// <summary>Runs a user mutation only while the circuit is online, interactive and idle; obsolete disabled-control callbacks are never queued.</summary>
    /// <param name="action">One server-authorized mutation using the operation cancellation token.</param>
    /// <returns>The immediate operation completion, or a completed task when controls cannot currently act.</returns>
    protected Task RunMutationAsync(Func<CancellationToken, Task> action) =>
        Busy || disposed ? Task.CompletedTask : RunAsync(action);

    /// <summary>Runs one cancellable operation, cancelling stale parameter loads and clearing content on access loss.</summary>
    /// <param name="action">Sequential renderer-context work accepting a cancellation token.</param>
    /// <returns>A task completing after state and error presentation have been updated.</returns>
    protected async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (disposed)
            return;
        if (operation is not null)
        {
            await operation.CancelAsync();
            operation.Dispose();
        }
        var current = new CancellationTokenSource();
        operation = current;
        var token = current.Token;
        Busy = true;
        Error = null;
        AccessDenied = false;
        try
        {
            await action(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A parameter change or disposal owns this cancellation; do not replace newer state.
        }
        catch (DomainException error)
        {
            if (error.Code == ErrorCode.Conflict)
                Logger.LogInformation("Event UI conflict in {Component}. Origin: {Origin}", GetType().Name, error.StackTrace);
            AccessDenied = error.Code is ErrorCode.Forbidden or ErrorCode.NotFound;
            if (AccessDenied)
                ClearProtectedState();
            Error = error.Code switch
            {
                ErrorCode.Forbidden => "Your session or workforce eligibility is no longer valid. Sign in again.",
                ErrorCode.NotFound => "This Event or membership item is unavailable to your account.",
                ErrorCode.Conflict => "This action is no longer allowed or the item changed. Reload and review the current state before retrying.",
                ErrorCode.Validation => error.Field switch
                {
                    "Reason" => "Enter a reason between 10 and 2,000 characters.",
                    "EndDate" or "StartDate" => "Check the Event date range, current end boundary, and existing Quest dates.",
                    "TimeZoneId" => "Choose a valid IANA time zone. Published Event zones cannot change.",
                    "User" => "Select an eligible workforce user. Removed memberships require an explicit individual restore.",
                    _ => "Some values are invalid. Check the form and selected directory identity."
                },
                _ => "The required service is unavailable or not configured. Existing individual memberships are unchanged."
            };
        }
        catch (Exception error)
        {
            ClearProtectedState();
            Logger.LogError(error, "Event UI operation failed.");
            Error = "The operation could not be completed. Reload before retrying.";
        }
        finally
        {
            if (ReferenceEquals(operation, current))
            {
                Busy = false;
                if (!token.IsCancellationRequested && experience is not null)
                    await experience.AfterOperationAsync();
            }
        }
    }

    /// <summary>Removes protected projections when the next server check denies access or an operation fails unexpectedly.</summary>
    protected virtual void ClearProtectedState()
    {
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        Revalidation.Reconnected -= ReconnectAsync;
        if (experience is not null)
            await experience.DisposeAsync();
        if (operation is not null)
        {
            await operation.CancelAsync();
            operation.Dispose();
            operation = null;
        }
        GC.SuppressFinalize(this);
    }
}
