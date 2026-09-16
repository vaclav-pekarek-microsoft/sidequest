using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.UnitTests.SecondaryExperience;
using Sidequest.Web.Components.Pages.Quests;
using Sidequest.Web.Components.Quests;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.CoreQuests;

/// <summary>Exercises Quest draft ownership, current-generation authorization, and callback isolation with controlled service gates.</summary>
public sealed class QuestManagementLifecycleTests : BunitContext
{
    private readonly ExperienceCoordinator experience = new();
    private readonly List<(Guid Id, string Version, QuestStatus Status, string Reason)> commands = [];
    private readonly List<(Guid Id, Guid Person, string Reason)> revocations = [];
    private readonly List<(Guid Id, ParticipationCommand Command)> participations = [];
    private readonly PersonSummary member = new(Guid.NewGuid(), "Selected member");
    private bool moderation = true;
    private QuestDetail detail = new(new(Guid.NewGuid(), Guid.NewGuid(), "Parent Event", "Quest title", "Room",
        new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
        "Europe/Prague", QuestStatus.Active, QuestVisibility.Private, null, null, null,
        ParticipationStatus.None, false, true, "original-version", null), "Authorized description", "", [], null, null, null);
    private TaskCompletionSource<QuestDetail>? readGate;
    private TaskCompletionSource? mutationGate;
    private TaskCompletionSource<IReadOnlyList<QuestHistoryItem>>? historyGate;
    private TaskCompletionSource<PageResult<MembershipSummary>>? memberGate;
    private Exception? readFailure;
    private DomainException? mutationFailure;
    private int reads;
    private int historyReads;
    private int memberReads;

    /// <summary>Registers real Fluent components and strict service boundaries without executing browser scripts or accessing SQL.</summary>
    public QuestManagementLifecycleTests()
    {
        Services.AddFluentUIComponents();
        Services.AddSingleton(experience);
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, _) =>
        {
            Assert.False(moderation);
            Assert.Equal(nameof(IEventService.ListMembersAsync), method.Name);
            memberReads++;
            return memberGate?.Task ?? Task.FromResult(MemberPage());
        }));
        Services.AddSingleton(SnapshotServiceProxy.Create<IQuestService>((method, arguments) =>
        {
            switch (method.Name)
            {
                case nameof(IQuestService.GetAsync):
                    Assert.Equal(detail.Summary.Id, arguments![0]);
                    Assert.Equal(moderation, arguments[1]);
                    reads++;
                    if (readFailure is not null)
                        return Task.FromException<QuestDetail>(readFailure);
                    return readGate?.Task ?? Task.FromResult(detail);
                case nameof(IQuestService.HistoryAsync):
                    historyReads++;
                    return historyGate?.Task ?? Task.FromResult<IReadOnlyList<QuestHistoryItem>>([]);
                case nameof(IQuestService.ChangeStatusAsync):
                    return ChangeStatusAsync(arguments!);
                case nameof(IQuestService.RevokeInvitationAsync):
                    revocations.Add(((Guid)arguments![0]!, (Guid)arguments[1]!, (string)arguments[2]!));
                    detail = detail with { Invitees = [] };
                    return Task.CompletedTask;
                case nameof(IQuestService.ParticipateAsync):
                    return ParticipateAsync(arguments!);
                default:
                    throw new InvalidOperationException($"Unexpected Quest operation: {method.Name}.");
            }
        }));
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
    }

    /// <summary>A same-session reconnect must retain the exact entered reason even when authorization yields, and must never replay an offline command.</summary>
    /// <param name="asynchronousRead">Whether the current authorization read yields until an explicit test-controlled release.</param>
    /// <returns>A task completing after reauthorization and one explicit mutation with the preserved input and original version.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameSessionRevalidationPreservesReasonAndRequiresExplicitCommand(bool asynchronousRead)
    {
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id)
            .Add(component => component.Moderation, true));
        const string reason = "Preserve this reviewed moderation reason.";
        page.Find("fluent-text-area").Input(reason);
        var execute = page.FindComponent<QuestManagement>().Instance.Execute;

        await experience.ReportConnectionAsync(false, null);
        await page.InvokeAsync(() => execute.InvokeAsync(new("suspend", null, reason)));
        Assert.Empty(commands);
        if (asynchronousRead)
            readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var reconnect = experience.ReportConnectionAsync(true, null);
        if (asynchronousRead)
        {
            page.WaitForAssertion(() => Assert.Equal(2, reads));
            Assert.False(reconnect.IsCompleted);
            await page.InvokeAsync(() => execute.InvokeAsync(new("suspend", null, reason)));
            Assert.Empty(page.FindComponents<QuestManagement>());
            Assert.DoesNotContain("Authorized description", page.Markup);
            Assert.Empty(commands);
            readGate!.SetResult(detail);
        }
        await reconnect;
        readGate = null;

        Assert.Empty(commands);
        Assert.Equal(reason, page.FindComponent<FluentTextArea>().Instance.Value);
        page.Find("input[type=checkbox]").Change(true);
        await page.InvokeAsync(() => page.FindComponent<FluentButton>().Instance.OnClick.InvokeAsync());

        var command = Assert.Single(commands);
        Assert.Equal((detail.Summary.Id, "original-version", QuestStatus.Suspended, reason), command);
        Assert.Equal(QuestStatus.Suspended, page.FindComponent<QuestManagement>().Instance.Detail.Summary.Status);
        Assert.Contains("Change saved. Required delivery will be attempted durably.", page.Markup);
        Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
        await experience.ReportConnectionAsync(false, null);
        await experience.ReportConnectionAsync(true, null);
        Assert.Single(commands);
    }

    /// <summary>Validation and concurrency failures retain the entered reason but consume confirmation; a conflict still requires explicit reload.</summary>
    /// <param name="code">The application failure category returned without a committed status change.</param>
    /// <returns>A task completing after preserved failed input, correct command blocking, and an explicit successful recovery.</returns>
    [Theory]
    [InlineData(ErrorCode.Validation)]
    [InlineData(ErrorCode.Conflict)]
    public async Task RejectedCommandRetainsReasonUntilExplicitRecovery(ErrorCode code)
    {
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, true));
        page.Find("fluent-text-area").Input("short");
        mutationFailure = new(code, "Reason validation or version check failed.");
        await ConfirmAsync(page);
        Assert.Equal("short", Assert.Single(commands).Reason);
        Assert.Equal(QuestStatus.Active, detail.Summary.Status);
        Assert.Equal("short", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.False(page.Find("input[type=checkbox]").HasAttribute("checked"));
        Assert.Contains("Reason validation or version check failed.", page.Find("[role=alert]").TextContent);
        mutationFailure = null;
        if (code == ErrorCode.Conflict)
        {
            var management = page.FindComponent<QuestManagement>().Instance;
            Assert.True(management.Busy);
            await page.InvokeAsync(() => management.Execute.InvokeAsync(new("suspend", null, "Must not overwrite.")));
            Assert.Single(commands);
            await ReloadAsync(page);
            Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
        }
        page.Find("fluent-text-area").Input("Reviewed and corrected reason.");
        await ConfirmAsync(page);
        Assert.Equal(2, commands.Count);
        Assert.Equal("Reviewed and corrected reason.", commands[1].Reason);
        Assert.Equal(QuestStatus.Suspended, detail.Summary.Status);
        Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
    }

    /// <summary>Confirmed access loss clears protected content and retained input, including when a later authorized reload succeeds.</summary>
    /// <param name="code">The current resource authorization failure that invalidates the old draft.</param>
    /// <returns>A task completing after redaction, rejected stale intent, and a fresh empty management draft.</returns>
    [Theory]
    [InlineData(ErrorCode.Forbidden)]
    [InlineData(ErrorCode.NotFound)]
    public async Task RevalidationAccessLossClearsDraftAndRejectsRetainedCommand(ErrorCode code)
    {
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, true));
        page.Find("fluent-text-area").Input("Old confidential reason.");
        var execute = page.FindComponent<QuestManagement>().Instance.Execute;
        await experience.ReportConnectionAsync(false, null);
        readFailure = new DomainException(code, "This Quest is unavailable.");
        await experience.ReportConnectionAsync(true, null);
        Assert.Empty(page.FindComponents<QuestManagement>());
        Assert.DoesNotContain("Authorized description", page.Markup);
        Assert.DoesNotContain("Old confidential reason.", page.Markup);
        await page.InvokeAsync(() => execute.InvokeAsync(new("suspend", null, "Old confidential reason.")));
        Assert.Empty(commands);
        readFailure = null;
        await experience.ReportConnectionAsync(false, null);
        await experience.ReportConnectionAsync(true, null);
        Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Empty(commands);
    }

    /// <summary>A changed service version retains local input but cannot silently use a new concurrency token for an old intention.</summary>
    /// <returns>A task completing after blocked stale intent and one explicitly reloaded command with the current version.</returns>
    [Fact]
    public async Task ChangedVersionKeepsReasonButRequiresExplicitReload()
    {
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, true));
        page.Find("fluent-text-area").Input("Reason for the original version.");
        await experience.ReportConnectionAsync(false, null);
        detail = detail with { Summary = detail.Summary with { Version = "current-version" } };
        await experience.ReportConnectionAsync(true, null);
        Assert.Equal("Reason for the original version.", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.True(page.FindComponent<QuestManagement>().Instance.Busy);
        await page.InvokeAsync(() => page.FindComponent<QuestManagement>().Instance.Execute.InvokeAsync(new("suspend", null, "Must not overwrite.")));
        Assert.Empty(commands);
        await ReloadAsync(page);
        Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
        page.Find("fluent-text-area").Input("Reviewed current state.");
        await ConfirmAsync(page);
        Assert.Equal("current-version", Assert.Single(commands).Version);
        Assert.Equal(QuestStatus.Suspended, detail.Summary.Status);
    }

    /// <summary>Callbacks from a replaced route cannot insert a previous reason or mutate the newly selected Quest.</summary>
    /// <returns>A task completing after stale input and command rejection followed by a current-route explicit mutation.</returns>
    [Fact]
    public async Task PreviousRouteCannotTransferDraftOrCommandToAnotherQuest()
    {
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, true));
        page.Find("fluent-text-area").Input("Reason for a different Quest.");
        var old = page.FindComponent<QuestManagement>().Instance;
        var execute = old.Execute;
        var changed = old.DraftChanged;
        detail = detail with { Summary = detail.Summary with { Id = Guid.NewGuid() } };
        page.Render(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        await page.InvokeAsync(() => changed.InvokeAsync(new("", "Late old reason.")));
        await page.InvokeAsync(() => execute.InvokeAsync(new("suspend", null, "Late old command.")));
        Assert.Empty(commands);
        Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
        page.Find("fluent-text-area").Input("Current Quest reason.");
        await ConfirmAsync(page);
        Assert.Equal(detail.Summary.Id, Assert.Single(commands).Id);
        Assert.Equal("Current Quest reason.", commands[0].Reason);
    }

    /// <summary>Reconnect waits for a pending mutation rather than refreshing over its state or replaying the user's command.</summary>
    /// <returns>A task completing after the gated mutation and one fresh read-only check, without publishing a now-obsolete post-command refresh.</returns>
    [Fact]
    public async Task RevalidationWaitsForPendingMutationWithoutReplayingIt()
    {
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, true));
        page.Find("fluent-text-area").Input("One committed status change.");
        mutationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = ConfirmAsync(page);
        page.WaitForAssertion(() => Assert.Single(commands));
        await experience.ReportConnectionAsync(false, null);
        var reconnect = experience.ReportConnectionAsync(true, null);
        Assert.False(submit.IsCompleted);
        Assert.False(reconnect.IsCompleted);
        Assert.Equal(1, reads);
        mutationGate.SetResult();
        await Task.WhenAll(submit, reconnect);
        Assert.Single(commands);
        Assert.Equal(2, reads);
        Assert.Equal(QuestStatus.Suspended, page.FindComponent<QuestManagement>().Instance.Detail.Summary.Status);
        Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
    }

    /// <summary>Owner management preserves both the selected person and reason while reauthorization removes the child controls.</summary>
    /// <returns>A task completing after one exact invitation revocation and a refreshed empty invitation projection, with no replay on reconnect.</returns>
    [Fact]
    public async Task OwnerRevalidationPreservesSelectedPersonAndExactRevocationReason()
    {
        moderation = false;
        detail = detail with { Summary = detail.Summary with { IsOwner = true }, Invitees = [member] };
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        page.Find("select").Change(member.Id.ToString());
        page.Find("fluent-text-area").Input("Remove this invitation with reviewed intent.");
        await experience.ReportConnectionAsync(false, null);
        await experience.ReportConnectionAsync(true, null);
        Assert.Equal(member.Id.ToString(), page.Find("select").GetAttribute("value"));
        Assert.Equal("Remove this invitation with reviewed intent.", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Empty(revocations);
        page.Find("input[type=checkbox]").Change(true);
        await page.InvokeAsync(() => page.FindComponents<FluentButton>()
            .Single(button => button.Markup.Contains("Revoke invitation", StringComparison.Ordinal)).Instance.OnClick.InvokeAsync());
        Assert.Equal((detail.Summary.Id, member.Id, "Remove this invitation with reviewed intent."), Assert.Single(revocations));
        Assert.Empty(page.FindComponent<QuestManagement>().Instance.Detail.Invitees!);
        Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Equal("", page.Find("select").GetAttribute("value"));
        await experience.ReportConnectionAsync(false, null);
        await experience.ReportConnectionAsync(true, null);
        Assert.Single(revocations);
    }

    /// <summary>A command completing after route replacement cannot refresh or clear the new Quest's unsent management input.</summary>
    /// <returns>A task completing after the original command finishes without an additional read or state change on the replacement view.</returns>
    [Fact]
    public async Task LateMutationCompletionCannotClearAnotherQuestDraft()
    {
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, true));
        var originalId = detail.Summary.Id;
        page.Find("fluent-text-area").Input("Old Quest mutation.");
        mutationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = ConfirmAsync(page);
        page.WaitForAssertion(() => Assert.Single(commands));
        detail = detail with { Summary = detail.Summary with { Id = Guid.NewGuid() } };
        page.Render(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        page.Find("fluent-text-area").Input("Keep this different Quest draft.");
        mutationGate.SetResult();
        await submit;
        Assert.Equal(originalId, Assert.Single(commands).Id);
        Assert.Equal(2, reads);
        Assert.Equal("Keep this different Quest draft.", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Equal(QuestStatus.Active, page.FindComponent<QuestManagement>().Instance.Detail.Summary.Status);
        Assert.DoesNotContain("Change saved.", page.Markup);
    }

    /// <summary>An indeterminate revalidation failure hides protected data and blocks commands without treating a recoverable draft as revoked.</summary>
    /// <param name="versionChanged">Whether the authorized resource changed during the read outage, requiring explicit conflict recovery.</param>
    /// <returns>A task completing after a later successful authorization restores the original reason without replaying a mutation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainRevalidationHidesContentAndRetainsDraftUntilAuthorized(bool versionChanged)
    {
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, true));
        page.Find("fluent-text-area").Input("Reason retained during a read outage.");
        var execute = page.FindComponent<QuestManagement>().Instance.Execute;
        await experience.ReportConnectionAsync(false, null);
        readFailure = new InvalidOperationException("Read unavailable.");
        await experience.ReportConnectionAsync(true, null);
        Assert.Empty(page.FindComponents<QuestManagement>());
        Assert.DoesNotContain("Authorized description", page.Markup);
        await page.InvokeAsync(() => execute.InvokeAsync(new("suspend", null, "Not authorized yet.")));
        Assert.Empty(commands);
        readFailure = null;
        if (versionChanged)
            detail = detail with { Summary = detail.Summary with { Version = "changed-during-outage" } };
        await experience.ReportConnectionAsync(false, null);
        await experience.ReportConnectionAsync(true, null);
        Assert.Equal("Reason retained during a read outage.", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Empty(commands);
        if (versionChanged)
        {
            Assert.True(page.FindComponent<QuestManagement>().Instance.Busy);
            await page.InvokeAsync(() => page.FindComponent<QuestManagement>().Instance.Execute.InvokeAsync(new("suspend", null, "Not reviewed.")));
            Assert.Empty(commands);
            await ReloadAsync(page);
            Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
            return;
        }
        await ConfirmAsync(page);
        Assert.Equal("Reason retained during a read outage.", Assert.Single(commands).Reason);
    }

    /// <summary>Read-only reauthorization adopts the latest version without inventing a management intention or fencing ordinary participation.</summary>
    /// <param name="viewer">Ordinary participant, Quest owner, or dedicated Event-owner moderator.</param>
    /// <param name="versionChanged">Whether the service returns a different version, compared with the unchanged-version control.</param>
    /// <returns>A task completing after current controls perform one explicit, authorized action without an unnecessary reload.</returns>
    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("ordinary", true)]
    [InlineData("owner", false)]
    [InlineData("owner", true)]
    [InlineData("moderator", false)]
    [InlineData("moderator", true)]
    public async Task NoDraftVersionChangeDoesNotInventConflict(string viewer, bool versionChanged)
    {
        moderation = viewer == "moderator";
        detail = detail with { Summary = detail.Summary with { IsOwner = viewer == "owner" } };
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, moderation));
        await experience.ReportConnectionAsync(false, null);
        if (versionChanged)
            detail = detail with { Summary = detail.Summary with { Version = "latest-version" } };
        await experience.ReportConnectionAsync(true, null);
        Assert.Empty(page.FindAll("[role=alert]"));
        if (moderation)
        {
            Assert.False(page.FindComponent<QuestManagement>().Instance.Busy);
            page.Find("fluent-text-area").Input("New intention for current state.");
            await ConfirmAsync(page);
            Assert.Equal(detail.Summary.Version, Assert.Single(commands).Version);
        }
        else
        {
            var participation = page.FindComponent<QuestParticipationControls>().Instance;
            Assert.False(participation.Busy);
            await page.InvokeAsync(() => participation.Change.InvokeAsync(ParticipationCommand.Join));
            Assert.Equal((detail.Summary.Id, ParticipationCommand.Join), Assert.Single(participations));
            Assert.Equal(ParticipationStatus.Joined, page.FindComponent<QuestParticipationControls>().Instance.Summary.Participation);
        }
    }

    /// <summary>Losing ownership discards obsolete management intent while current ordinary access remains usable.</summary>
    /// <param name="versionChanged">Whether ownership loss also changes the projected Quest version.</param>
    /// <returns>A task completing after one ordinary participation change and a later ownership restoration with an empty, usable draft.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipLossDiscardsDraftWithoutFencingParticipation(bool versionChanged)
    {
        moderation = false;
        detail = detail with { Summary = detail.Summary with { IsOwner = true } };
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        page.Find("fluent-text-area").Input("Old owner intention.");
        await experience.ReportConnectionAsync(false, null);
        detail = detail with { Summary = detail.Summary with
        {
            IsOwner = false, Version = versionChanged ? "ownership-changed" : detail.Summary.Version
        } };
        await experience.ReportConnectionAsync(true, null);
        Assert.Empty(page.FindComponents<QuestManagement>());
        Assert.Empty(page.FindAll("[role=alert]"));
        var participation = page.FindComponent<QuestParticipationControls>().Instance;
        Assert.False(participation.Busy);
        await page.InvokeAsync(() => participation.Change.InvokeAsync(ParticipationCommand.Join));
        Assert.Single(participations);
        await experience.ReportConnectionAsync(false, null);
        detail = detail with { Summary = detail.Summary with { IsOwner = true, Version = "ownership-restored" } };
        await experience.ReportConnectionAsync(true, null);
        Assert.False(page.FindComponent<QuestManagement>().Instance.Busy);
        Assert.Equal("", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Single(participations);
    }

    /// <summary>An obsolete authorization result cannot publish data, finish an earlier reconnect report, or unblock commands ahead of the newest check.</summary>
    /// <param name="stage">The old detail, history, or member query held across a newer disconnect/reconnect generation.</param>
    /// <param name="outcome">The newest read succeeds, denies access, or fails without establishing revocation.</param>
    /// <returns>A task completing after both reports await the current generation, with exact draft retention or clearing and no replay.</returns>
    [Theory]
    [InlineData("detail", "success")]
    [InlineData("detail", "denied")]
    [InlineData("detail", "failure")]
    [InlineData("history", "success")]
    [InlineData("history", "denied")]
    [InlineData("history", "failure")]
    [InlineData("members", "success")]
    [InlineData("members", "denied")]
    [InlineData("members", "failure")]
    public async Task OverlappingReconnectsAwaitNewestAuthorization(string stage, string outcome)
    {
        moderation = stage != "members";
        detail = detail with { Summary = detail.Summary with { IsOwner = !moderation } };
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters
            .Add(component => component.Id, detail.Summary.Id).Add(component => component.Moderation, moderation));
        var obsoleteWasPublished = false;
        page.OnMarkupUpdated += (_, _) =>
            obsoleteWasPublished |= page.Markup.Contains("Obsolete protected projection", StringComparison.Ordinal);
        const string reason = "Keep intent across current authorization.";
        page.Find("fluent-text-area").Input(reason);
        var execute = page.FindComponent<QuestManagement>().Instance.Execute;
        var authorized = detail;
        detail = detail with { Description = "Obsolete protected projection" };
        var oldDetail = new TaskCompletionSource<QuestDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldHistory = new TaskCompletionSource<IReadOnlyList<QuestHistoryItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldMembers = new TaskCompletionSource<PageResult<MembershipSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        readGate = stage == "detail" ? oldDetail : null;
        historyGate = stage == "history" ? oldHistory : null;
        memberGate = stage == "members" ? oldMembers : null;
        await experience.ReportConnectionAsync(false, null);
        var first = experience.ReportConnectionAsync(true, null);
        page.WaitForAssertion(() => Assert.Equal(2, stage == "detail" ? reads : stage == "history" ? historyReads : memberReads));
        await experience.ReportConnectionAsync(false, null);
        var second = experience.ReportConnectionAsync(true, null);
        var current = new TaskCompletionSource<QuestDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        readGate = current;
        historyGate = null;
        memberGate = null;
        var attemptedDuringPendingCheck = false;
        experience.SnapshotRefreshRequested += async () =>
        {
            obsoleteWasPublished |= page.Markup.Contains("Obsolete protected projection", StringComparison.Ordinal);
            if (!current.Task.IsCompleted && !attemptedDuringPendingCheck)
            {
                attemptedDuringPendingCheck = true;
                await page.InvokeAsync(() => execute.InvokeAsync(new("suspend", null, reason)));
            }
        };
        oldDetail.TrySetResult(detail);
        oldHistory.TrySetResult([]);
        oldMembers.TrySetResult(MemberPage());
        try
        {
            page.WaitForAssertion(() => Assert.Equal(3, reads));
            await page.InvokeAsync(() => { });
            Assert.Empty(commands);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(obsoleteWasPublished);
            Assert.Empty(page.FindComponents<QuestManagement>());
            Assert.DoesNotContain("Obsolete protected projection", page.Markup);
            await page.InvokeAsync(() => execute.InvokeAsync(new("suspend", null, reason)));
            Assert.Empty(commands);
        }
        finally
        {
            detail = authorized;
            if (outcome == "denied")
                current.TrySetException(new DomainException(ErrorCode.Forbidden, "Current access denied."));
            else if (outcome == "failure")
                current.TrySetException(new InvalidOperationException("Current read unavailable."));
            else
                current.TrySetResult(authorized);
            await Task.WhenAll(first, second);
            readGate = null;
        }
        Assert.False(obsoleteWasPublished);
        Assert.Empty(commands);
        if (outcome != "success")
        {
            Assert.Empty(page.FindComponents<QuestManagement>());
            Assert.DoesNotContain("Authorized description", page.Markup);
            await experience.ReportConnectionAsync(false, null);
            await experience.ReportConnectionAsync(true, null);
        }
        Assert.Equal(outcome == "denied" ? "" : reason, page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.False(page.FindComponent<QuestManagement>().Instance.Busy);
        Assert.Empty(commands);
    }

    /// <summary>A late participation completion and its captured callback cannot mutate or refresh a replacement Quest's draft.</summary>
    /// <returns>A task completing after one mutation on the old Quest, no extra read, and unchanged input on the replacement route.</returns>
    [Fact]
    public async Task LateParticipationCannotClearReplacementQuestDraft()
    {
        moderation = false;
        detail = detail with { Summary = detail.Summary with { IsOwner = true } };
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        var originalId = detail.Summary.Id;
        var change = page.FindComponent<QuestParticipationControls>().Instance.Change;
        mutationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = page.InvokeAsync(() => change.InvokeAsync(ParticipationCommand.Join));
        page.WaitForAssertion(() => Assert.Single(participations));
        detail = detail with { Summary = detail.Summary with { Id = Guid.NewGuid() } };
        page.Render(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        page.Find("fluent-text-area").Input("Replacement Quest draft.");
        mutationGate.SetResult();
        await operation;
        Assert.Equal(2, reads);
        Assert.Equal("Replacement Quest draft.", page.FindComponent<FluentTextArea>().Instance.Value);
        await page.InvokeAsync(() => change.InvokeAsync(ParticipationCommand.Leave));
        Assert.Equal((originalId, ParticipationCommand.Join), Assert.Single(participations));
        Assert.Equal(2, reads);
        Assert.Equal("Replacement Quest draft.", page.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Equal(ParticipationStatus.None, page.FindComponent<QuestParticipationControls>().Instance.Summary.Participation);
        Assert.DoesNotContain("Participation saved.", page.Markup);
    }

    /// <summary>Paging and input callbacks belong to the rendered authorized view, not a replacement route, explicit reload, or reconnect projection.</summary>
    /// <param name="replacement">The lifecycle transition that replaces the originating view without authorizing its old callbacks.</param>
    /// <returns>A task completing after obsolete callbacks are rejected and a current paging callback preserves the current draft.</returns>
    [Theory]
    [InlineData("route")]
    [InlineData("reload")]
    [InlineData("reconnect")]
    public async Task OldPagingAndDraftCallbacksCannotAffectReplacementView(string replacement)
    {
        moderation = false;
        detail = detail with { Summary = detail.Summary with { IsOwner = true } };
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        var old = page.FindComponent<QuestManagement>().Instance;
        var changed = old.DraftChanged;
        var paging = old.PageChanged;
        if (replacement == "route")
        {
            detail = detail with { Summary = detail.Summary with { Id = Guid.NewGuid() } };
            page.Render(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        }
        else if (replacement == "reload")
        {
            mutationFailure = new(ErrorCode.Validation, "Explicit recovery available.");
            await page.InvokeAsync(() => old.Execute.InvokeAsync(new("suspend", null, "Rejected.")));
            await ReloadAsync(page);
        }
        else
        {
            await experience.ReportConnectionAsync(false, null);
            await experience.ReportConnectionAsync(true, null);
        }
        page.Find("fluent-text-area").Input("Current view intention.");
        var expectedReads = memberReads;
        await page.InvokeAsync(() => changed.InvokeAsync(new("", "Obsolete input.")));
        await page.InvokeAsync(() => paging.InvokeAsync(2));
        Assert.Equal(expectedReads, memberReads);
        Assert.Equal("Current view intention.", page.FindComponent<FluentTextArea>().Instance.Value);
        var current = page.FindComponent<QuestManagement>().Instance;
        Assert.Equal(1, current.MemberPage);
        await page.InvokeAsync(() => current.PageChanged.InvokeAsync(2));
        Assert.Equal(expectedReads + 1, memberReads);
        Assert.Equal(2, page.FindComponent<QuestManagement>().Instance.MemberPage);
        Assert.Equal("Current view intention.", page.FindComponent<FluentTextArea>().Instance.Value);
    }

    /// <summary>Only retained input owns a stale-intent version: clearing a draft removes that fence, while a person-only intention retains it.</summary>
    /// <param name="retainSelection">Whether the selected person remains an unsent intention after the reason is cleared.</param>
    /// <returns>A task completing after a changed version either permits a fresh command or requires explicit reload for the retained person.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearedDraftAndPersonOnlyIntentionHaveDistinctVersionSemantics(bool retainSelection)
    {
        moderation = false;
        detail = detail with { Summary = detail.Summary with { IsOwner = true } };
        await experience.ReportConnectionAsync(true, null);
        var page = Render<QuestDetails>(parameters => parameters.Add(component => component.Id, detail.Summary.Id));
        page.Find("select").Change(member.Id.ToString());
        page.Find("fluent-text-area").Input("Intention subsequently cleared.");
        page.Find("fluent-text-area").Input("");
        if (!retainSelection)
            page.Find("select").Change("");
        await experience.ReportConnectionAsync(false, null);
        detail = detail with { Summary = detail.Summary with { Version = "newest-version" } };
        await experience.ReportConnectionAsync(true, null);
        var management = page.FindComponent<QuestManagement>().Instance;
        Assert.Equal(retainSelection, management.Busy);
        Assert.False(page.FindComponent<QuestParticipationControls>().Instance.Busy);
        Assert.Equal(retainSelection ? member.Id.ToString() : "", page.Find("select").GetAttribute("value"));
        if (retainSelection)
        {
            await page.InvokeAsync(() => management.Execute.InvokeAsync(new("revoke", member.Id, "Not reviewed for the new version.")));
            Assert.Empty(revocations);
            await ReloadAsync(page);
            Assert.Equal("", page.Find("select").GetAttribute("value"));
        }
        else
        {
            Assert.Empty(page.FindAll("[role=alert]"));
            await page.InvokeAsync(() => management.Execute.InvokeAsync(new("suspend", null, "New reviewed intention.")));
            Assert.Equal("newest-version", Assert.Single(commands).Version);
        }
    }

    private PageResult<MembershipSummary> MemberPage() => new([new(member, MembershipStatus.Active, false)], 1, 1, 25);

    private async Task ParticipateAsync(object?[] arguments)
    {
        var id = (Guid)arguments[0]!;
        var command = (ParticipationCommand)arguments[1]!;
        participations.Add((id, command));
        if (mutationGate is not null)
            await mutationGate.Task;
        if (detail.Summary.Id == id)
            detail = detail with { Summary = detail.Summary with
            {
                Participation = command == ParticipationCommand.Join ? ParticipationStatus.Joined : ParticipationStatus.None
            } };
    }

    private async Task ChangeStatusAsync(object?[] arguments)
    {
        var original = detail;
        commands.Add(((Guid)arguments[0]!, (string)arguments[1]!, (QuestStatus)arguments[2]!, (string)arguments[3]!));
        if (mutationGate is not null)
            await mutationGate.Task;
        if (mutationFailure is not null)
            throw mutationFailure;
        if (detail.Summary.Id == original.Summary.Id)
            detail = detail with { Summary = detail.Summary with { Status = (QuestStatus)arguments[2]! } };
    }

    private static Task ConfirmAsync(IRenderedComponent<QuestDetails> page)
    {
        page.Find("input[type=checkbox]").Change(true);
        return page.InvokeAsync(() => page.FindComponents<FluentButton>()
            .Single(button => button.Markup.Contains(">Suspend", StringComparison.Ordinal)).Instance.OnClick.InvokeAsync());
    }

    private static Task ReloadAsync(IRenderedComponent<QuestDetails> page) =>
        page.InvokeAsync(() => page.FindComponents<FluentButton>()
            .Single(button => button.Markup.Contains("Reload current state", StringComparison.Ordinal)).Instance.OnClick.InvokeAsync());
}
