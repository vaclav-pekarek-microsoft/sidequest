using Microsoft.AspNetCore.Components;
using NodaTime;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;

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

    /// <summary>Existing Quest identifier, or null for draft creation.</summary>
    [Parameter] public Guid? Id { get; set; }
    /// <summary>Optional Event preselection supplied by an authorized Event link.</summary>
    [SupplyParameterFromQuery(Name = "eventId")] public Guid? EventId { get; set; }
    [Inject] private IQuestService Quests { get; set; } = default!;
    [Inject] private IEventService Events { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILogger<QuestEdit> Logger { get; set; } = default!;

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        navigationVersion++;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        var requestVersion = navigationVersion;
        initial = null;
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

    private Task SaveAsync(QuestEditorModel model) => RunAsync(async () =>
    {
        var input = model.ToInput();
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

    private async Task RunAsync(Func<Task> action)
    {
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
                initial = null;
                events = [];
            }
        }
        catch (Exception failure) when (requestVersion == navigationVersion)
        {
            initial = null;
            events = [];
            var correlationId = Guid.NewGuid().ToString("N");
            Logger.LogError(failure, "Quest editor operation failed. Correlation {CorrelationId}.", correlationId);
            error = $"The operation failed. Reload before trying again. Reference: {correlationId}.";
        }
        catch (Exception) when (requestVersion != navigationVersion) { }
        finally { if (requestVersion == navigationVersion) busy = false; }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
