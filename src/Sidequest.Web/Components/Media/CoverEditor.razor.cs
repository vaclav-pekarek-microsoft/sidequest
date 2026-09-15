using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Sidequest.Application.Media;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Components.Media;

/// <summary>Edits only a persisted Quest's cover; parent-owned text and rowversion are never mutated on failure.</summary>
public partial class CoverEditor : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly string helpId = $"cover-help-{Guid.NewGuid():N}";
    private bool busy;
    private bool disposed;
    private bool unavailable;
    private Guid displayedQuest;
    private string? error;
    private string? success;
    private bool Disabled => !RendererInfo.IsInteractive || IsBusy || busy || QuestId == Guid.Empty || disposed || unavailable;

    [Inject] private IMediaService Media { get; set; } = default!;

    /// <summary>Persisted Quest identifier; an empty value disables upload until the parent saves its draft.</summary>
    [Parameter, EditorRequired] public Guid QuestId { get; set; }

    /// <summary>Current Base64 rowversion captured at the start of each mutation.</summary>
    [Parameter, EditorRequired] public string Version { get; set; } = "";

    /// <summary>Assigned cover identifier owned by the parent; null displays the default cover.</summary>
    [Parameter] public Guid? AssetId { get; set; }

    /// <summary>Authorized Quest title for accessible cover alternative text.</summary>
    [Parameter, EditorRequired] public string QuestTitle { get; set; } = "";

    /// <summary>Disables cover changes while the parent performs another mutation.</summary>
    [Parameter] public bool IsBusy { get; set; }

    /// <summary>Returns the committed cover and rowversion; the parent updates these without replacing unsaved text.</summary>
    [Parameter] public EventCallback<CoverUpdate> CoverChanged { get; set; }

    /// <summary>Lets the parent disable conflicting save/publish actions for the duration of processing.</summary>
    [Parameter] public EventCallback<bool> BusyChanged { get; set; }

    /// <summary>Reports current access loss so the parent can clear its protected Quest fields; the editor also hides its cover.</summary>
    [Parameter] public EventCallback<ErrorCode> AccessLost { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (displayedQuest != QuestId)
        {
            displayedQuest = QuestId;
            unavailable = false;
            error = null;
            success = null;
        }
    }

    private async Task UploadAsync(InputFileChangeEventArgs args)
    {
        if (Disabled)
            return;
        var quest = QuestId;
        var version = Version;
        await ChangeAsync(async () =>
        {
            if (args.FileCount != 1)
                throw new DomainException(ErrorCode.Validation, "Choose one cover image.");
            await using var stream = args.File.OpenReadStream(2_097_152, lifetime.Token);
            return await Media.UploadCoverAsync(quest, version, stream, lifetime.Token);
        });
    }

    private async Task RemoveAsync()
    {
        if (Disabled)
            return;
        var quest = QuestId;
        var version = Version;
        await ChangeAsync(() => Media.RemoveCoverAsync(quest, version, lifetime.Token));
    }

    private async Task ChangeAsync(Func<Task<CoverUpdate>> change)
    {
        var target = QuestId;
        busy = true;
        error = null;
        success = null;
        try
        {
            await BusyChanged.InvokeAsync(true);
            var result = await change();
            if (!disposed && QuestId == target)
            {
                await CoverChanged.InvokeAsync(result);
                success = result.AssetId is null ? "Cover removed." : "Cover updated.";
            }
        }
        catch (DomainException exception)
        {
            if (!disposed && QuestId == target)
            {
                error = exception.Code == ErrorCode.Conflict
                    ? $"{exception.Message} Your text has been kept; check the current state before retrying."
                    : exception.Message;
                if (exception.Code is ErrorCode.NotFound or ErrorCode.Forbidden)
                {
                    unavailable = true;
                    await AccessLost.InvokeAsync(exception.Code);
                }
            }
        }
        catch (IOException)
        {
            error = "The image could not be read. Choose a file up to 2 MiB and try again.";
        }
        catch (OperationCanceledException)
        {
            error = "Cover processing was cancelled or timed out. No replacement was confirmed.";
        }
        finally
        {
            busy = false;
            if (!disposed)
                await BusyChanged.InvokeAsync(false);
        }
    }

    /// <summary>Cancels unfinished processing when the editor leaves the renderer; does not dispose caller-owned parameters.</summary>
    /// <returns>Completion after cooperative cancellation has been signaled.</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
