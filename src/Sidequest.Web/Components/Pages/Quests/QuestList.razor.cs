using Microsoft.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Pages.Quests;

/// <summary>Owns authorized, paginated Quest views and clears protected results after failed reauthorization.</summary>
public partial class QuestList : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<EventSummary> events = [];
    private PageResult<QuestSummary>? result;
    private QuestListKind kind;
    private string eventFilter = "";
    private int page = 1;
    private bool loading;
    private string? error;
    private int navigationVersion;
    private DateOnly? fromDate;
    private DateOnly? throughDate;
    private string dateZone = "Etc/UTC";
    private ExperienceViewSubscription? experience;

    [Inject] private IQuestService Quests { get; set; } = default!;
    [Inject] private IEventService Events { get; set; } = default!;
    [Inject] private ILogger<QuestList> Logger { get; set; } = default!;
    [Inject] private ExperienceCoordinator Experience { get; set; } = default!;
    /// <summary>Optional list view from a deep link; invalid values use Joined.</summary>
    [SupplyParameterFromQuery(Name = "view")] public string? View { get; set; }
    /// <summary>Optional internal Event filter, never an access grant.</summary>
    [SupplyParameterFromQuery(Name = "eventId")] public Guid? EventId { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized() =>
        experience = new(Experience, () => InvokeAsync(StateHasChanged), ReauthorizeAsync);

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender) =>
        RendererInfo.IsInteractive ? experience?.AfterRenderAsync() ?? Task.CompletedTask : Task.CompletedTask;

    private Task ReauthorizeAsync() => InvokeAsync(async () =>
    {
        if (lifetime.IsCancellationRequested)
            return;
        navigationVersion++;
        await LoadAsync();
        if (!lifetime.IsCancellationRequested)
            StateHasChanged();
    });

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        navigationVersion++;
        kind = Enum.TryParse<QuestListKind>(View, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : QuestListKind.Joined;
        eventFilter = EventId?.ToString() ?? "";
        page = 1;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var requestVersion = navigationVersion;
        loading = true;
        error = null;
        result = null;
        try
        {
            var nextEvents = (await Events.ListAsync(EventListKind.Mine, new PageRequest(1, 100), lifetime.Token)).Items;
            Guid? eventId = null;
            var nextZone = "Etc/UTC";
            if (eventFilter.Length > 0)
            {
                if (!Guid.TryParse(eventFilter, out var id))
                    throw new DomainException(ErrorCode.Validation, "Choose an Event.", "Event");
                eventId = id;
                var parent = nextEvents.SingleOrDefault(e => e.Id == id) ??
                    (await Events.GetAsync(id, lifetime.Token)).Summary;
                if (!nextEvents.Any(e => e.Id == id))
                    nextEvents = nextEvents.Append(parent).ToArray();
                nextZone = parent.TimeZoneId;
            }
            var dates = QuestDateFilterFactory.FromDates(fromDate, throughDate, nextZone);
            var next = await Quests.ListAsync(kind, eventId, new PageRequest(page), dates, lifetime.Token);
            if (requestVersion != navigationVersion || lifetime.IsCancellationRequested)
                return;
            events = nextEvents;
            dateZone = nextZone;
            result = next;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (DomainException failure) when (requestVersion == navigationVersion) { events = []; error = failure.Message; }
        catch (Exception failure) when (requestVersion == navigationVersion)
        {
            events = [];
            var correlationId = Guid.NewGuid().ToString("N");
            Logger.LogError(failure, "Quest list load failed. Correlation {CorrelationId}.", correlationId);
            error = $"Quests could not be loaded. Please retry. Reference: {correlationId}.";
        }
        catch (Exception) when (requestVersion != navigationVersion) { }
        finally
        {
            if (requestVersion == navigationVersion)
            {
                loading = false;
                if (experience is not null)
                    await experience.AfterOperationAsync();
            }
        }
    }

    private async Task ResetAsync() { page = 1; await LoadAsync(); }
    private async Task PreviousAsync() { page--; await LoadAsync(); }
    private async Task NextAsync() { page++; await LoadAsync(); }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (experience is not null)
            await experience.DisposeAsync();
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
