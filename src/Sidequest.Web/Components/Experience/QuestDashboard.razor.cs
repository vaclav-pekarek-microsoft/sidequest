using Microsoft.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Experience;
using Sidequest.Application.Quests;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Experience;

/// <summary>Owns authorized dashboard paging; rechecks access during interactive activation and reconnection rather than trusting prerendered state.</summary>
public partial class QuestDashboard : IAsyncDisposable
{
    private static readonly (QuestListKind Kind, string Label)[] ViewOptions =
    [
        (QuestListKind.Board, "All Quests"),
        (QuestListKind.Joined, "Upcoming Joined"),
        (QuestListKind.Following, "Following"),
        (QuestListKind.Organizing, "Organizing"),
        (QuestListKind.Discover, "Discover"),
        (QuestListKind.Invited, "Invited")
    ];
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<EventSummary> events = [];
    private DashboardPage? result;
    private DashboardFilter filter = new(QuestListKind.Board, null, null, null);
    private QuestLayout layout = QuestLayout.Board;
    private int page = 1;
    private int eventPage = 1;
    private int eventTotal;
    private int generation;
    private bool loading;
    private string? error;
    [Inject] private DashboardService Dashboard { get; set; } = default!;
    [Inject] private IEventService Events { get; set; } = default!;
    [Inject] private ExperienceCoordinator Coordinator { get; set; } = default!;
    [Inject] private ILogger<QuestDashboard> Logger { get; set; } = default!;
    private string Heading => filter.Kind switch
    {
        QuestListKind.Board => "Your Quest board",
        QuestListKind.Joined => "Upcoming Joined",
        _ => filter.Kind.ToString()
    };

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        Coordinator.Changed += OnConnectionAsync;
        Coordinator.ReauthorizationRequested += ReauthorizeAsync;
        await LoadAsync();
    }

    private Task OnConnectionAsync() => InvokeAsync(StateHasChanged);
    private Task ReauthorizeAsync() => InvokeAsync(async () =>
    {
        await LoadAsync();
        if (!lifetime.IsCancellationRequested) StateHasChanged();
    });
    private async Task FilterAsync(DashboardFilter next) { filter = next; page = 1; await LoadAsync(); }
    private async Task SelectKindAsync(QuestListKind kind)
    {
        filter = filter with { Kind = kind };
        page = 1;
        await LoadAsync();
    }
    private async Task PreviousAsync() { page--; await LoadAsync(); }
    private async Task NextAsync() { page++; await LoadAsync(); }
    private async Task EventPageAsync(int next) { eventPage = next; await LoadAsync(); }

    private async Task LoadAsync()
    {
        var request = ++generation;
        loading = true;
        result = null;
        error = null;
        try
        {
            var eventResult = await Events.ListAsync(EventListKind.Mine, new PageRequest(eventPage, 100), lifetime.Token);
            var nextEvents = eventResult.Items;
            var zone = "Etc/UTC";
            if (filter.EventId is { } id)
            {
                var selected = nextEvents.SingleOrDefault(e => e.Id == id) ?? (await Events.GetAsync(id, lifetime.Token)).Summary;
                if (!nextEvents.Any(e => e.Id == id)) nextEvents = nextEvents.Append(selected).ToArray();
                zone = selected.TimeZoneId;
            }
            var dates = QuestDateFilterFactory.FromDates(filter.From, filter.Through, zone);
            var next = await Dashboard.ListAsync(filter.Kind, filter.EventId, new PageRequest(page, filter.PageSize),
                dates, lifetime.Token);
            if (request != generation || lifetime.IsCancellationRequested) return;
            events = nextEvents;
            eventTotal = eventResult.TotalCount;
            result = next;
            if (RendererInfo.IsInteractive) await Coordinator.RequestSnapshotRefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (DomainException failure) when (request == generation)
        {
            if (failure.Code != ErrorCode.Validation) events = [];
            error = failure.Code == ErrorCode.Validation ? "Check the Event and date range. The last date must not precede the first." :
                "Quests are unavailable or access has changed. Reconnect and sign in if needed.";
            if (RendererInfo.IsInteractive) await Coordinator.RequestSnapshotRefreshAsync();
        }
        catch (Exception failure) when (request == generation)
        {
            events = [];
            var reference = Guid.NewGuid().ToString("N");
            Logger.LogError(failure, "Dashboard load failed. Reference {Reference}.", reference);
            error = $"Quests could not be loaded. Please retry. Reference: {reference}.";
        }
        catch (Exception) when (request != generation) { }
        finally { if (request == generation) loading = false; }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Coordinator.Changed -= OnConnectionAsync;
        Coordinator.ReauthorizationRequested -= ReauthorizeAsync;
        generation++;
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }

    private enum QuestLayout
    {
        Board,
        List
    }
}
