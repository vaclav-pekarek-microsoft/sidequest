using Microsoft.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Pages.Quests;

/// <summary>Composes ordinary or audited moderation details and refreshes protected data after every command.</summary>
public partial class QuestDetails : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationToken cancellationToken;
    private bool disposed;
    private QuestDetail? detail;
    private IReadOnlyList<QuestHistoryItem> history = [];
    private IReadOnlyList<PersonSummary> members = [];
    private int memberPage = 1;
    private bool moreMembers;
    private bool busy;
    private bool conflict;
    private bool draftConflict;
    private bool reauthorizationRequired;
    private int navigationVersion;
    private int authorizationVersion;
    private int completedAuthorizationVersion = -1;
    private string? error;
    private string? message;
    private ExperienceViewSubscription? experience;
    private QuestManagementDraft managementDraft = new();
    private string? draftVersion;
    private Task pendingOperation = Task.CompletedTask;
    private bool ControlsDisabled => disposed || busy || reauthorizationRequired ||
        !RendererInfo.IsInteractive || !Experience.CanUseOnlineActions;
    private bool HasManagementIntention => managementDraft.Reason.Length > 0 || managementDraft.SelectedPerson.Length > 0;

    /// <summary>Quest route identifier, reauthorized for every load and command.</summary>
    [Parameter] public Guid Id { get; set; }
    /// <summary>Explicit audited Event-owner view; it never grants ordinary private participation access.</summary>
    [Parameter] public bool Moderation { get; set; }
    [Inject] private IQuestService Quests { get; set; } = default!;
    [Inject] private IEventService Events { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILogger<QuestDetails> Logger { get; set; } = default!;
    [Inject] private ExperienceCoordinator Experience { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        cancellationToken = lifetime.Token;
        experience = new(Experience, ConnectionChangedAsync, ReauthorizeAsync);
    }

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender) =>
        RendererInfo.IsInteractive ? experience?.AfterRenderAsync() ?? Task.CompletedTask : Task.CompletedTask;

    private Task ConnectionChangedAsync() => InvokeAsync(() =>
    {
        if (disposed)
            return;
        if (!Experience.CanUseOnlineActions)
        {
            authorizationVersion++;
            reauthorizationRequired = true;
        }
        StateHasChanged();
    });

    private Task ReauthorizeAsync() => InvokeAsync(async () =>
    {
        var requestVersion = navigationVersion;
        authorizationVersion++;
        reauthorizationRequired = true;
        // Only read-only checks repeat when a newer connection invalidates an awaited result.
        do
        {
            while (!pendingOperation.IsCompleted)
                await pendingOperation;
            if (disposed || requestVersion != navigationVersion || !Experience.CanUseOnlineActions ||
                completedAuthorizationVersion == authorizationVersion)
                return;
            var checkVersion = authorizationVersion;
            await RunAsync(() => RefreshAsync(preserveDraft: true), preserveDraftOnFailure: true, authorizationCheck: true);
            if (disposed || requestVersion != navigationVersion)
                return;
            StateHasChanged();
            if (checkVersion == authorizationVersion)
                return;
        }
        while (Experience.CanUseOnlineActions);
    });

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        navigationVersion++;
        memberPage = 1;
        return LoadAsync();
    }

    private Task LoadAsync()
    {
        authorizationVersion++;
        reauthorizationRequired = true;
        conflict = false;
        return RunAsync(() => RefreshAsync(), authorizationCheck: true);
    }

    private Task ReloadAsync(int requestVersion, int checkVersion) =>
        !IsCurrent(requestVersion, checkVersion) || busy || !RendererInfo.IsInteractive || !Experience.CanUseOnlineActions
            ? Task.CompletedTask : LoadAsync();

    private async Task RefreshAsync(bool preserveDraft = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestVersion = navigationVersion;
        var checkVersion = authorizationVersion;
        var questId = Id;
        var moderation = Moderation;
        Clear(preserveDraft);
        if (preserveDraft)
            StateHasChanged();
        var next = await Quests.GetAsync(questId, moderation, cancellationToken);
        if (!IsCurrent(requestVersion, checkVersion))
            return;
        var nextHistory = await Quests.HistoryAsync(questId, moderation, cancellationToken);
        if (!IsCurrent(requestVersion, checkVersion))
            return;
        PageResult<MembershipSummary>? nextMembers = null;
        if (next.Summary.IsOwner && !moderation)
        {
            nextMembers = await Events.ListMembersAsync(next.Summary.EventId, new PageRequest(memberPage), cancellationToken);
            if (!IsCurrent(requestVersion, checkVersion))
                return;
        }
        detail = next;
        history = nextHistory;
        ApplyMembers(nextMembers);
        if (!moderation && !next.Summary.IsOwner)
        {
            if (HasManagementIntention)
                conflict = false;
            ClearManagementDraft();
        }
        draftConflict = preserveDraft && HasManagementIntention && draftVersion is not null && draftVersion != next.Summary.Version;
        if (draftConflict)
            error = "The Quest changed while disconnected. Your unsent management input is kept; reload current state before making changes.";
        else if (conflict)
            error = "The previous operation conflicted. Reload current state before making changes.";
        reauthorizationRequired = false;
    }

    private Task ParticipateAsync(int requestVersion, int checkVersion, ParticipationCommand command) =>
        !IsCurrent(requestVersion, checkVersion) || ControlsDisabled || conflict || detail is null
        ? Task.CompletedTask : RunAsync(async () =>
    {
        await Quests.ParticipateAsync(Id, command, cancellationToken);
        if (!IsCurrent(requestVersion, checkVersion))
            return;
        await RefreshAsync();
        if (IsCurrent(requestVersion, checkVersion))
            message = "Participation saved. Applicable delivery is queued, not guaranteed to have arrived.";
    }, mutation: true);

    private Task ExecuteAsync(int requestVersion, int checkVersion, QuestActionRequest request) =>
        !IsCurrent(requestVersion, checkVersion) || ControlsDisabled || conflict || draftConflict
        ? Task.CompletedTask : RunAsync(async () =>
    {
        if (detail is null)
            return;
        var version = detail.Summary.Version;
        var token = cancellationToken;
        switch (request.Action)
        {
            case "publish":
            case "reinstate": await Quests.ChangeStatusAsync(Id, version, QuestStatus.Active, request.Reason, token); break;
            case "suspend": await Quests.ChangeStatusAsync(Id, version, QuestStatus.Suspended, request.Reason, token); break;
            case "cancel": await Quests.ChangeStatusAsync(Id, version, QuestStatus.Cancelled, request.Reason, token); break;
            case "archive": await Quests.ChangeStatusAsync(Id, version, QuestStatus.Archived, request.Reason, token); break;
            case "delete":
                await Quests.DeleteDraftAsync(Id, version, token);
                if (requestVersion != navigationVersion || disposed)
                    return;
                Clear();
                if (IsCurrent(requestVersion, checkVersion))
                    Navigation.NavigateTo("/quests?view=Organizing");
                return;
            case "invite": await Quests.InviteAsync(Id, Target(request), token); break;
            case "revoke": await Quests.RevokeInvitationAsync(Id, Target(request), request.Reason, token); break;
            case "remove-attendee": await Quests.RemoveAttendeeAsync(Id, Target(request), request.Reason, token); break;
            case "add-owner": await Quests.AddOwnerAsync(Id, Target(request), token); break;
            case "remove-owner": await Quests.RemoveOwnerAsync(Id, Target(request), token); break;
            default: throw new DomainException(ErrorCode.Validation, "Choose a supported action.");
        }
        if (requestVersion != navigationVersion || disposed)
            return;
        ClearManagementDraft();
        if (!IsCurrent(requestVersion, checkVersion))
            return;
        await RefreshAsync();
        if (IsCurrent(requestVersion, checkVersion))
            message = "Change saved. Required delivery will be attempted durably.";
    }, mutation: true);

    private static Guid Target(QuestActionRequest request) => request.UserId ??
        throw new DomainException(ErrorCode.Validation, "Choose a current Event member.");

    private Task LoadMembersAsync(int requestVersion, int checkVersion, int page) =>
        !IsCurrent(requestVersion, checkVersion) || ControlsDisabled || conflict || draftConflict || detail is null ||
        !detail.Summary.IsOwner || Moderation ? Task.CompletedTask : RunAsync(async () =>
    {
        memberPage = page;
        await FetchMembersAsync();
    });

    private async Task FetchMembersAsync()
    {
        if (detail is null || Moderation)
            return;
        var requestVersion = navigationVersion;
        var checkVersion = authorizationVersion;
        var eventId = detail.Summary.EventId;
        var result = await Events.ListMembersAsync(eventId, new PageRequest(memberPage), cancellationToken);
        if (!IsCurrent(requestVersion, checkVersion))
            return;
        ApplyMembers(result);
    }

    private void ApplyMembers(PageResult<MembershipSummary>? result)
    {
        members = result?.Items.Where(m => m.Status == MembershipStatus.Active).Select(m => m.User).ToArray() ?? [];
        moreMembers = result is not null && memberPage * 25 < result.TotalCount;
    }

    private bool IsCurrent(int requestVersion, int checkVersion) =>
        !disposed && !cancellationToken.IsCancellationRequested &&
        requestVersion == navigationVersion && checkVersion == authorizationVersion;

    private async Task RunAsync(Func<Task> action, bool preserveDraftOnFailure = false, bool authorizationCheck = false, bool mutation = false)
    {
        if (disposed)
            return;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOperation = completed.Task;
        var requestVersion = navigationVersion;
        var checkVersion = authorizationVersion;
        busy = true;
        error = null;
        message = null;
        try { await action(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (DomainException failure) when (!disposed && requestVersion == navigationVersion && (mutation || checkVersion == authorizationVersion))
        {
            error = failure.Message;
            conflict = failure.Code == ErrorCode.Conflict;
            if (failure.Code is ErrorCode.Forbidden or ErrorCode.NotFound)
                Clear();
        }
        catch (Exception failure) when (!disposed && requestVersion == navigationVersion && (mutation || checkVersion == authorizationVersion))
        {
            Clear(preserveDraftOnFailure);
            var correlationId = Guid.NewGuid().ToString("N");
            Logger.LogError(failure, "Quest detail operation failed. Correlation {CorrelationId}.", correlationId);
            error = $"The operation failed. Reload current state before trying again. Reference: {correlationId}.";
        }
        catch (Exception) when (!IsCurrent(requestVersion, checkVersion)) { }
        finally
        {
            if (requestVersion == navigationVersion)
            {
                busy = false;
                if (authorizationCheck && IsCurrent(requestVersion, checkVersion))
                    completedAuthorizationVersion = checkVersion;
            }
            completed.TrySetResult();
        }
        if (experience is not null && IsCurrent(requestVersion, checkVersion) && !reauthorizationRequired)
            await experience.AfterOperationAsync();
    }

    private void SetManagementDraft(int requestVersion, int checkVersion, QuestManagementDraft draft)
    {
        if (IsCurrent(requestVersion, checkVersion) && !ControlsDisabled && detail is not null)
        {
            managementDraft = draft;
            draftVersion = HasManagementIntention ? draftVersion ?? detail.Summary.Version : null;
            if (!HasManagementIntention)
                draftConflict = false;
        }
    }

    private void Clear(bool preserveDraft = false)
    {
        detail = null;
        history = [];
        members = [];
        if (!preserveDraft)
            ClearManagementDraft();
    }

    private void ClearManagementDraft()
    {
        managementDraft = new();
        draftVersion = null;
        draftConflict = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        navigationVersion++;
        if (experience is not null)
            await experience.DisposeAsync();
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
