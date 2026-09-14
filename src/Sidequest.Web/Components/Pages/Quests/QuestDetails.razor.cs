using Microsoft.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;

namespace Sidequest.Web.Components.Pages.Quests;

/// <summary>Composes ordinary or audited moderation details and refreshes protected data after every command.</summary>
public partial class QuestDetails : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private QuestDetail? detail;
    private IReadOnlyList<QuestHistoryItem> history = [];
    private IReadOnlyList<PersonSummary> members = [];
    private int memberPage = 1;
    private bool moreMembers;
    private bool busy;
    private bool conflict;
    private int navigationVersion;
    private string? error;
    private string? message;

    /// <summary>Quest route identifier, reauthorized for every load and command.</summary>
    [Parameter] public Guid Id { get; set; }
    /// <summary>Explicit audited Event-owner view; it never grants ordinary private participation access.</summary>
    [SupplyParameterFromQuery(Name = "moderation")] public bool Moderation { get; set; }
    [Inject] private IQuestService Quests { get; set; } = default!;
    [Inject] private IEventService Events { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILogger<QuestDetails> Logger { get; set; } = default!;

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        navigationVersion++;
        memberPage = 1;
        return LoadAsync();
    }

    private Task LoadAsync()
    {
        conflict = false;
        return RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var requestVersion = navigationVersion;
        var questId = Id;
        var moderation = Moderation;
        Clear();
        var next = await Quests.GetAsync(questId, moderation, lifetime.Token);
        var nextHistory = await Quests.HistoryAsync(questId, moderation, lifetime.Token);
        if (requestVersion != navigationVersion || lifetime.IsCancellationRequested)
            return;
        detail = next;
        history = nextHistory;
        if (next.Summary.IsOwner && !Moderation)
            await FetchMembersAsync();
    }

    private Task ParticipateAsync(ParticipationCommand command) => RunAsync(async () =>
    {
        await Quests.ParticipateAsync(Id, command, lifetime.Token);
        await RefreshAsync();
        message = "Participation saved. Applicable delivery is queued, not guaranteed to have arrived.";
    });

    private Task ExecuteAsync(QuestActionRequest request) => RunAsync(async () =>
    {
        if (detail is null)
            return;
        var version = detail.Summary.Version;
        var token = lifetime.Token;
        switch (request.Action)
        {
            case "publish":
            case "reinstate": await Quests.ChangeStatusAsync(Id, version, QuestStatus.Active, request.Reason, token); break;
            case "suspend": await Quests.ChangeStatusAsync(Id, version, QuestStatus.Suspended, request.Reason, token); break;
            case "cancel": await Quests.ChangeStatusAsync(Id, version, QuestStatus.Cancelled, request.Reason, token); break;
            case "archive": await Quests.ChangeStatusAsync(Id, version, QuestStatus.Archived, request.Reason, token); break;
            case "delete":
                await Quests.DeleteDraftAsync(Id, version, token);
                Clear();
                Navigation.NavigateTo("/quests?view=Organizing");
                return;
            case "invite": await Quests.InviteAsync(Id, Target(request), token); break;
            case "revoke": await Quests.RevokeInvitationAsync(Id, Target(request), request.Reason, token); break;
            case "remove-attendee": await Quests.RemoveAttendeeAsync(Id, Target(request), request.Reason, token); break;
            case "add-owner": await Quests.AddOwnerAsync(Id, Target(request), token); break;
            case "remove-owner": await Quests.RemoveOwnerAsync(Id, Target(request), token); break;
            default: throw new DomainException(ErrorCode.Validation, "Choose a supported action.");
        }
        await RefreshAsync();
        message = "Change saved. Required delivery will be attempted durably.";
    });

    private static Guid Target(QuestActionRequest request) => request.UserId ??
        throw new DomainException(ErrorCode.Validation, "Choose a current Event member.");

    private Task LoadMembersAsync(int page) => RunAsync(async () =>
    {
        memberPage = page;
        await FetchMembersAsync();
    });

    private async Task FetchMembersAsync()
    {
        if (detail is null || Moderation)
            return;
        var requestVersion = navigationVersion;
        var eventId = detail.Summary.EventId;
        var result = await Events.ListMembersAsync(eventId, new PageRequest(memberPage), lifetime.Token);
        if (requestVersion != navigationVersion || lifetime.IsCancellationRequested)
            return;
        members = result.Items.Where(m => m.Status == MembershipStatus.Active).Select(m => m.User).ToArray();
        moreMembers = memberPage * 25 < result.TotalCount;
    }

    private async Task RunAsync(Func<Task> action)
    {
        var requestVersion = navigationVersion;
        busy = true;
        error = null;
        message = null;
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (DomainException failure) when (requestVersion == navigationVersion)
        {
            error = failure.Message;
            conflict = failure.Code == ErrorCode.Conflict;
            if (failure.Code is ErrorCode.Forbidden or ErrorCode.NotFound)
                Clear();
        }
        catch (Exception failure) when (requestVersion == navigationVersion)
        {
            Clear();
            var correlationId = Guid.NewGuid().ToString("N");
            Logger.LogError(failure, "Quest detail operation failed. Correlation {CorrelationId}.", correlationId);
            error = $"The operation failed. Reload current state before trying again. Reference: {correlationId}.";
        }
        catch (Exception) when (requestVersion != navigationVersion) { }
        finally { if (requestVersion == navigationVersion) busy = false; }
    }

    private void Clear() { detail = null; history = []; members = []; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
