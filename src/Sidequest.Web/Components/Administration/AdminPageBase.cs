using Microsoft.AspNetCore.Components;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Components.Administration;

/// <summary>Owns cancellation and safe operation feedback shared by focused administration pages.</summary>
public abstract class AdminPageBase : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>Current operation state; all interactive controls also gate on RendererInfo.IsInteractive.</summary>
    protected bool Busy { get; private set; }

    /// <summary>Safe current error including explicit conflict guidance without discarding unsaved text.</summary>
    protected string? Error { get; private set; }

    /// <summary>Whether the latest server check denied administrator access.</summary>
    protected bool Forbidden { get; private set; }

    /// <summary>Last successful operation outcome for accessible feedback.</summary>
    protected string Status { get; set; } = "";

    /// <summary>Cancellation token tied to the component's lifetime.</summary>
    protected CancellationToken Lifetime => lifetime.Token;

    /// <summary>Prevents duplicate submissions and inert prerender clicks.</summary>
    protected bool Disabled => Busy || !RendererInfo.IsInteractive;

    /// <summary>Executes a cancellation-aware operation, retaining edits on conflict and clearing sensitive state on access loss.</summary>
    /// <param name="operation">Application-service callback; it must reauthorize each request.</param>
    /// <returns>Completion of the operation or handled safe failure.</returns>
    protected async Task RunAsync(Func<Task> operation)
    {
        if (Busy || lifetime.IsCancellationRequested)
            return;
        Busy = true;
        Error = null;
        Status = "";
        try
        {
            await operation();
            Forbidden = false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (DomainException exception)
        {
            Error = exception.Message;
            if (exception.Code is ErrorCode.Forbidden or ErrorCode.NotFound)
            {
                Forbidden = true;
                ClearSensitiveState();
            }
        }
        catch (Exception)
        {
            Error = "Administration is unavailable. No success has been confirmed; check your session and reload before retrying.";
            ClearSensitiveState();
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Clears previously loaded sensitive state when a server operation no longer authorizes it.</summary>
    protected abstract void ClearSensitiveState();

    /// <summary>Cancels pending SQL/application calls and releases the component's cancellation source.</summary>
    /// <returns>Completion of cancellation; does not request a render during disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
