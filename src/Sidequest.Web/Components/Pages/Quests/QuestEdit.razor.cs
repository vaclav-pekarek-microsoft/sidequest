using Microsoft.AspNetCore.Components;
using NodaTime;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Media;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Pages.Quests;

/// <summary>Loads service-owned editor snapshots and prevents silent overwrite after rowversion conflicts.</summary>
public partial class QuestEdit : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<EventSummary> events = [];
    private QuestInput? initial;
    private string selectedEvent = "";
    private string zone = "";
    private string version = "";
    private bool published;
    private bool busy;
    private bool conflict;
    private string? error;
    private int navigationVersion;
    private int editorGeneration;
    private Guid? coverAssetId;
    private bool coverBusy;
    private ExperienceViewSubscription? experience;
    private Task pendingOperation = Task.CompletedTask;

    /// <summary>Existing Quest identifier, or null for draft creation.</summary>
    [Parameter] public Guid? Id { get; set; }
    /// <summary>Optional Event preselection supplied by an authorized Event link.</summary>
    [Parameter] public Guid? EventId { get; set; }
    [Inject] private IQuestService Quests { get; set; } = default!;
    [Inject] private IEventService Events { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILogger<QuestEdit> Logger { get; set; } = default!;
    [Inject] private ExperienceCoordinator Experience { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized() =>
        experience = new(Experience, () => InvokeAsync(StateHasChanged), ReauthorizeAsync);

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender) =>
        RendererInfo.IsInteractive ? experience?.AfterRenderAsync() ?? Task.CompletedTask : Task.CompletedTask;

    private Task ReauthorizeAsync() => InvokeAsync(async () =>
    {
        var requestVersion = navigationVersion;
        await pendingOperation;
        if (lifetime.IsCancellationRequested || requestVersion != navigationVersion)
            return;
        await RunAsync(async () =>
        {
            var generation = editorGeneration;
            if (Id is { } id)
            {
                var current = (await Quests.GetAsync(id, cancellationToken: lifetime.Token)).Summary;
                if (generation != editorGeneration || lifetime.IsCancellationRequested)
                    return;
                if (!current.IsOwner)
                    throw new DomainException(ErrorCode.NotFound, "This Quest is unavailable.");
                if (current.Version != version || current.Status is not (QuestStatus.Draft or QuestStatus.Active or QuestStatus.Suspended))
                    throw new DomainException(ErrorCode.Conflict, "The Quest changed while disconnected. Your text is kept; reload the current version before saving.");
            }
            else if (Guid.TryParse(selectedEvent, out var eventId))
            {
                var current = (await Events.GetAsync(eventId, lifetime.Token)).Summary;
                if (generation != editorGeneration || lifetime.IsCancellationRequested)
                    return;
                if (!current.IsMember || current.Status != EventStatus.Active)
                    throw new DomainException(ErrorCode.NotFound, "This Event is unavailable for Quest creation.");
            }
            else
            {
                await Events.ListAsync(EventListKind.Mine, new PageRequest(1, 100), lifetime.Token);
            }
        });
        if (!lifetime.IsCancellationRequested)
            StateHasChanged();
    });

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        navigationVersion++;
        coverBusy = false;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (coverBusy)
            return;
        var requestVersion = navigationVersion;
        ClearEditor();
        conflict = false;
        await RunAsync(async () =>
        {
            if (Id is not null)
            {
                var detail = await Quests.GetAsync(Id.Value, cancellationToken: lifetime.Token);
                if (requestVersion != navigationVersion || lifetime.IsCancellationRequested)
                    return;
                if (!detail.Summary.IsOwner)
                    throw new DomainException(ErrorCode.NotFound, "This resource is unavailable.");
                var summary = detail.Summary;
                if (summary.Status is not (QuestStatus.Draft or QuestStatus.Active or QuestStatus.Suspended))
                    throw new DomainException(ErrorCode.Conflict, "This Quest is read-only.");
                zone = summary.TimeZoneId;
                version = summary.Version;
                coverAssetId = summary.CoverAssetId;
                published = summary.Status != QuestStatus.Draft;
                var start = Instant.FromDateTimeOffset(summary.StartUtc).InZone(TimeRules.Zone(zone));
                var end = Instant.FromDateTimeOffset(summary.EndUtc).InZone(TimeRules.Zone(zone));
                initial = new(summary.Title, detail.Description, summary.Location, summary.SuggestedCapacity,
                    start.LocalDateTime.ToDateTimeUnspecified(), end.LocalDateTime.ToDateTimeUnspecified(),
                    start.Offset.ToTimeSpan(), end.Offset.ToTimeSpan(), summary.Visibility);
            }
            else
            {
                var nextEvents = (await Events.ListAsync(EventListKind.Mine, new PageRequest(1, 100), lifetime.Token)).Items;
                if (requestVersion != navigationVersion || lifetime.IsCancellationRequested)
                    return;
                events = nextEvents.Where(e => e.Status == EventStatus.Active && e.IsMember).ToArray();
                selectedEvent = EventId?.ToString() ?? selectedEvent;
                if (Guid.TryParse(selectedEvent, out var selectedId) && !events.Any(e => e.Id == selectedId))
                {
                    var selected = (await Events.GetAsync(selectedId, lifetime.Token)).Summary;
                    if (requestVersion != navigationVersion || lifetime.IsCancellationRequested)
                        return;
                    if (!selected.IsMember || selected.Status != EventStatus.Active)
                        throw new DomainException(ErrorCode.NotFound, "This Event is unavailable for Quest creation.");
                    events = events.Append(selected).ToArray();
                }
                SetInitialEvent();
            }
        });
    }

    private Task SelectEventAsync()
    {
        error = null;
        SetInitialEvent();
        return Task.CompletedTask;
    }

    private void SetInitialEvent()
    {
        var parent = events.SingleOrDefault(e => e.Id.ToString() == selectedEvent);
        initial = null;
        if (parent is null)
            return;
        zone = parent.TimeZoneId;
        var start = parent.StartDate.ToDateTime(new TimeOnly(9, 0));
        initial = new("", "", "", null, start, start.AddHours(1), null, null, QuestVisibility.Public);
    }

    private async Task SaveAsync(QuestEditorModel model)
    {
        if (busy || coverBusy || conflict || !RendererInfo.IsInteractive || !Experience.CanUseOnlineActions)
            return;
        await RunAsync(async () =>
        {
            var input = model.ToInput(zone);
            if (Id is null)
            {
                var id = await Quests.CreateAsync(Guid.Parse(selectedEvent), input, lifetime.Token);
                Navigation.NavigateTo($"/quests/{id}");
            }
            else
            {
                await Quests.EditAsync(Id.Value, version, input, lifetime.Token);
                Navigation.NavigateTo($"/quests/{Id}");
            }
        });
    }

    private async Task UpdateCover(int generation, CoverUpdate update)
    {
        if (generation != editorGeneration || initial is null || lifetime.IsCancellationRequested)
            return;
        coverAssetId = update.AssetId;
        version = update.Version;
        if (experience is not null)
            await experience.AfterOperationAsync();
    }

    private void SetCoverBusy(int generation, bool value)
    {
        if (generation == editorGeneration && !lifetime.IsCancellationRequested)
            coverBusy = value;
    }

    private void CoverConflict(int generation)
    {
        if (generation != editorGeneration || lifetime.IsCancellationRequested)
            return;
        conflict = true;
        error = "The Quest changed during the cover operation. Your text is kept. Reload the current version before making further changes.";
    }

    private async Task CoverAccessLost(int generation, ErrorCode code)
    {
        if (generation != editorGeneration || lifetime.IsCancellationRequested)
            return;
        ClearEditor();
        error = code == ErrorCode.Forbidden ? "Your access to this Quest has changed." : "This Quest is unavailable.";
        if (experience is not null)
            await experience.AfterOperationAsync();
    }

    private void ClearEditor()
    {
        editorGeneration++;
        initial = null;
        events = [];
        coverBusy = false;
        coverAssetId = null;
        version = "";
        zone = "";
        published = false;
    }

    private async Task RunAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOperation = completed.Task;
        var requestVersion = navigationVersion;
        busy = true;
        error = null;
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (DomainException failure) when (requestVersion == navigationVersion)
        {
            error = failure.Message;
            conflict = failure.Code == ErrorCode.Conflict;
            if (failure.Code is ErrorCode.Forbidden or ErrorCode.NotFound)
            {
                ClearEditor();
            }
        }
        catch (Exception failure) when (requestVersion == navigationVersion)
        {
            ClearEditor();
            var correlationId = Guid.NewGuid().ToString("N");
            Logger.LogError(failure, "Quest editor operation failed. Correlation {CorrelationId}.", correlationId);
            error = $"The operation failed. Reload before trying again. Reference: {correlationId}.";
        }
        catch (Exception) when (requestVersion != navigationVersion) { }
        finally
        {
            try
            {
                if (requestVersion == navigationVersion)
                {
                    busy = false;
                    if (experience is not null)
                        await experience.AfterOperationAsync();
                }
            }
            finally { completed.TrySetResult(); }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (experience is not null)
            await experience.DisposeAsync();
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
