using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Notifications;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.UnitTests.SecondaryExperience;
using Sidequest.Web.Components.Notifications;
using Sidequest.Web.Components.Pages.Notifications;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.CoreDelivery;

/// <summary>Exercises notification views against awaited reconnects, revoked access and non-cooperative application operations.</summary>
public sealed class NotificationReconnectTests : BunitContext
{
    private readonly NotificationUiService service = new();
    private readonly ExperienceCoordinator experience = new();
    private readonly ErrorLogger logger = new();
    private readonly Guid eventId = Guid.NewGuid();
    private int eventQueries;
    private bool eventMember = true;

    /// <summary>Registers real Fluent controls and deterministic authorized query seams; no database or browser transport is used.</summary>
    public NotificationReconnectTests()
    {
        Services.AddFluentUIComponents();
        Services.AddSingleton<INotificationService>(service);
        Services.AddSingleton(experience);
        Services.AddSingleton<ILogger<NotificationViewBase>>(logger);
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, _) =>
        {
            Assert.Equal(nameof(IEventService.GetAsync), method.Name);
            eventQueries++;
            return Task.FromResult(new EventDetail(new(eventId, "Event", "Summary",
                new(2026, 1, 1), new(2026, 12, 31), "Europe/Prague", EventStatus.Active,
                [], eventMember, false, "version"), null));
        }));
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
        service.Items = Inbox("old protected notification");
        service.Unread = 1;
        service.Failures = [new(Guid.NewGuid(), "delivery", "old failure", 2, DateTimeOffset.UnixEpoch)];
    }

    /// <summary>Captured callbacks are dropped offline and reconnect performs only new reads, never queued mark/replay/preference mutations.</summary>
    /// <returns>Completion after all three views reconnect and exact mutation counts remain zero.</returns>
    [Fact]
    public async Task OfflineCallbacksNeverMutateOrReplay()
    {
        await experience.ReportConnectionAsync(true, null);
        var inbox = Render<NotificationInbox>();
        var failures = Render<NotificationFailures>();
        var preferences = Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId));
        var mark = Button(inbox, "Mark read").Instance.OnClick;
        var markAll = Button(inbox, "Mark all read").Instance.OnClick;
        await ClickAsync(failures, "Review replay");
        var replay = Button(failures, "Confirm replay").Instance.OnClick;
        var form = preferences.FindComponent<EditForm>().Instance;
        var save = form.OnValidSubmit;
        var saveEvent = Button(preferences, "Save Event override").Instance.OnClick;

        await experience.ReportConnectionAsync(false, null);
        Assert.True(Button(inbox, "Refresh").Instance.Disabled);
        Assert.True(Button(failures, "Refresh failures").Instance.Disabled);
        Assert.True(Button(preferences, "Retry").Instance.Disabled);
        Assert.DoesNotContain("old protected notification", inbox.Markup);
        Assert.DoesNotContain("Confirm replay", failures.Markup);
        Assert.Empty(preferences.FindAll("form"));
        await inbox.InvokeAsync(() => mark.InvokeAsync());
        await inbox.InvokeAsync(() => markAll.InvokeAsync());
        await failures.InvokeAsync(() => replay.InvokeAsync());
        await preferences.InvokeAsync(() => save.InvokeAsync(form.EditContext));
        await preferences.InvokeAsync(() => saveEvent.InvokeAsync());
        AssertNoMutations();

        await experience.ReportConnectionAsync(true, null);
        AssertNoMutations();
        Assert.Equal(2, service.ListCalls);
        Assert.Equal(2, service.CountCalls);
        Assert.Equal(2, service.FailureCalls);
        Assert.Equal(2, service.PreferenceCalls);
        Assert.Equal(2, eventQueries);
        Assert.DoesNotContain("Confirm replay", failures.Markup);
    }

    /// <summary>Prerender explicitly disables native and Fluent controls, and even direct event dispatch cannot reach mutation services.</summary>
    /// <returns>Completion after direct prerender callbacks have been rejected.</returns>
    [Fact]
    public async Task PrerenderControlsAndCallbacksStayDisabled()
    {
        SetRendererInfo(new("Static", false));
        await experience.ReportConnectionAsync(true, null);
        var inbox = Render<NotificationInbox>();
        var failures = Render<NotificationFailures>();
        var preferences = Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId));
        Assert.All(inbox.FindComponents<FluentButton>(), button => Assert.True(button.Instance.Disabled));
        Assert.All(failures.FindComponents<FluentButton>(), button => Assert.True(button.Instance.Disabled));
        Assert.All(preferences.FindComponents<FluentButton>(), button => Assert.True(button.Instance.Disabled));
        Assert.All(preferences.FindComponents<FluentCheckbox>(), checkbox => Assert.True(checkbox.Instance.Disabled));
        Assert.True(preferences.FindComponent<FluentTextField>().Instance.Disabled);
        Assert.True(preferences.Find("fieldset").HasAttribute("disabled"));
        await ClickAsync(inbox, "Mark all read");
        await ClickAsync(failures, "Review replay");
        await ClickAsync(preferences, "Save Event override");
        await SubmitAsync(preferences);
        Assert.DoesNotContain("Confirm replay", failures.Markup);
        AssertNoMutations();
    }

    /// <summary>Reconnect replaces the inbox only after both privacy-filtered list and unread queries finish, blocking intermediate actions.</summary>
    /// <returns>Completion after the dependent unread count releases and the exact current projection is displayed.</returns>
    [Fact]
    public async Task InboxReconnectAwaitsListAndUnreadBeforeRevealingCurrentItems()
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationInbox>();
        await experience.ReportConnectionAsync(false, null);
        service.Items = Inbox("new authorized notification");
        service.UnreadQuery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.UnreadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnect = experience.ReportConnectionAsync(true, null);
        await WaitForStartAsync(service.UnreadStarted);
        Assert.Equal(2, service.CountCalls);
        Assert.False(reconnect.IsCompleted);
        Assert.DoesNotContain("old protected notification", component.Markup);
        Assert.DoesNotContain("new authorized notification", component.Markup);
        Assert.True(Button(component, "Mark all read").Instance.Disabled);
        service.UnreadQuery.SetResult(7);
        await reconnect;
        Assert.Contains("new authorized notification", component.Markup);
        Assert.Contains("7 unread", component.Markup);
        Assert.DoesNotContain("old protected notification", component.Markup);
    }

    /// <summary>Confirmed recipient or administrator revocation removes stale lists, unread state and pending replay without leaking service diagnostics.</summary>
    /// <param name="code">The confirmed access-loss category returned by the current application query.</param>
    /// <returns>Completion after both protected list views have reauthorized and cleared their projections.</returns>
    [Theory]
    [InlineData(ErrorCode.Forbidden)]
    [InlineData(ErrorCode.NotFound)]
    public async Task RevokedInboxAndAdministratorStateAreCleared(ErrorCode code)
    {
        await experience.ReportConnectionAsync(true, null);
        var inbox = Render<NotificationInbox>();
        var failures = Render<NotificationFailures>();
        await ClickAsync(failures, "Review replay");
        await experience.ReportConnectionAsync(false, null);
        service.ReadFailure = new DomainException(code, "private-secret");
        await experience.ReportConnectionAsync(true, null);
        Assert.Equal(2, service.ListCalls);
        Assert.Equal(2, service.FailureCalls);
        Assert.Null(Field(inbox.Instance, "items"));
        Assert.Equal(0, Field(inbox.Instance, "unread"));
        Assert.Null(Field(failures.Instance, "failures"));
        Assert.Null(Field(failures.Instance, "pending"));
        Assert.DoesNotContain("old protected notification", inbox.Markup);
        Assert.DoesNotContain("old failure", failures.Markup);
        Assert.DoesNotContain("Confirm replay", failures.Markup);
        Assert.DoesNotContain("private-secret", inbox.Markup + failures.Markup);
        Assert.Contains("no longer has access", inbox.Find("[role=alert]").TextContent);
        Assert.Contains("no longer has access", failures.Find("[role=alert]").TextContent);
    }

    /// <summary>A valid persisted administrator reconnect refreshes failures and invalidates the previous confirmation and captured review item.</summary>
    /// <returns>Completion after the new redacted result is visible with no stale confirmation.</returns>
    [Fact]
    public async Task AdministratorReconnectRefreshesFailuresAndDiscardsStaleConfirmation()
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationFailures>();
        var oldReview = Button(component, "Review replay").Instance.OnClick;
        await ClickAsync(component, "Review replay");
        var oldConfirm = Button(component, "Confirm replay").Instance.OnClick;
        await experience.ReportConnectionAsync(false, null);
        service.Failures = [new(Guid.NewGuid(), "scheduled", "current failure", 4, DateTimeOffset.UnixEpoch)];
        await experience.ReportConnectionAsync(true, null);
        await component.InvokeAsync(() => oldReview.InvokeAsync());
        await component.InvokeAsync(() => oldConfirm.InvokeAsync());
        Assert.Equal(2, service.FailureCalls);
        Assert.Contains("current failure", component.Markup);
        Assert.DoesNotContain("old failure", component.Markup);
        Assert.DoesNotContain("Confirm replay", component.Markup);
        Assert.Empty(service.Replays);
    }

    /// <summary>Current user and Event authorization are read again while the same unsaved form and independent override remain untouched.</summary>
    /// <returns>Completion after reconnect and explicit saves verify every retained editable value.</returns>
    [Fact]
    public async Task PreferencesReconnectPreservesUnsavedModelAndEventOverride()
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId));
        var model = EditDraft(component);
        await component.InvokeAsync(() => component.FindComponents<FluentCheckbox>().Last().Instance.ValueChanged.InvokeAsync(true));
        await experience.ReportConnectionAsync(false, null);
        await experience.ReportConnectionAsync(true, null);
        Assert.Equal(2, service.PreferenceCalls);
        Assert.Equal(2, eventQueries);
        Assert.Same(model, component.FindComponent<EditForm>().Instance.Model);
        Assert.True(component.FindComponents<FluentCheckbox>().Last().Instance.Value);
        Assert.False(component.FindComponent<FluentTextField>().Instance.Disabled);
        Assert.False(component.Find("fieldset").HasAttribute("disabled"));
        await SubmitAsync(component);
        await ClickAsync(component, "Save Event override");
        Assert.Equal(new PreferenceInput(true, false, false, .75m, "Europe/Prague"), service.Saved);
        Assert.Equal((eventId, true), Assert.Single(service.EventOverrides));
    }

    /// <summary>Confirmed user revocation deletes both the draft and Event override; a later valid check loads defaults rather than resurrecting input.</summary>
    /// <param name="code">The confirmed identity/resource denial returned by the preference query.</param>
    /// <returns>Completion after denial, state deletion and a fresh explicit read-only retry.</returns>
    [Theory]
    [InlineData(ErrorCode.Forbidden)]
    [InlineData(ErrorCode.NotFound)]
    public async Task PreferencesRevocationClearsDraftAndOverride(ErrorCode code)
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId));
        var original = EditDraft(component);
        await component.InvokeAsync(() => component.FindComponents<FluentCheckbox>().Last().Instance.ValueChanged.InvokeAsync(true));
        await experience.ReportConnectionAsync(false, null);
        service.ReadFailure = new DomainException(code, "private-secret");
        await experience.ReportConnectionAsync(true, null);
        Assert.Null(Field(component.Instance, "model"));
        Assert.Equal(false, Field(component.Instance, "eventEnabled"));
        Assert.Empty(component.FindAll("form"));
        Assert.DoesNotContain("private-secret", component.Markup);
        service.ReadFailure = null;
        await ClickAsync(component, "Retry");
        var reloaded = Assert.IsType<NotificationPreferenceForm>(component.FindComponent<EditForm>().Instance.Model);
        Assert.NotSame(original, reloaded);
        Assert.Equal(1m, reloaded.ReminderHours);
        Assert.False(component.FindComponents<FluentCheckbox>().Last().Instance.Value);
    }

    /// <summary>Loss of Event membership is denied even when the user preference query still succeeds and Event discovery remains available.</summary>
    /// <returns>Completion after the independent Event access check has removed the full protected draft.</returns>
    [Fact]
    public async Task PreferencesReconnectRechecksEventMembershipAndClearsRevokedDraft()
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId));
        EditDraft(component);
        await experience.ReportConnectionAsync(false, null);
        eventMember = false;
        await experience.ReportConnectionAsync(true, null);
        Assert.Equal(2, service.PreferenceCalls);
        Assert.Equal(2, eventQueries);
        Assert.Null(Field(component.Instance, "model"));
        Assert.Empty(component.FindAll("form"));
        Assert.Empty(service.EventOverrides);
    }

    /// <summary>Changing the Event route cannot carry a prior override or authorize a different resource using the previous query.</summary>
    /// <returns>Completion after the new resource is checked and its denied draft is removed.</returns>
    [Fact]
    public async Task PreferenceEventNavigationReauthorizesAndDiscardsPreviousDraft()
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId));
        EditDraft(component);
        await component.InvokeAsync(() => component.FindComponents<FluentCheckbox>().Last().Instance.ValueChanged.InvokeAsync(true));
        eventMember = false;
        component.Render(p => p.Add(x => x.EventId, Guid.NewGuid()));
        Assert.Equal(2, service.PreferenceCalls);
        Assert.Equal(2, eventQueries);
        Assert.Null(Field(component.Instance, "model"));
        Assert.Equal(false, Field(component.Instance, "eventEnabled"));
        Assert.Empty(component.FindAll("form"));
        AssertNoMutations();
    }

    /// <summary>A transient reconnect failure hides unverified edits, logs the unexpected error, and preserves exact draft identity for successful retry.</summary>
    /// <returns>Completion after the safe retry makes the unchanged draft editable without an implicit save.</returns>
    [Fact]
    public async Task TransientPreferenceReauthorizationFailsClosedAndRetainsDraftForRetry()
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId));
        var original = EditDraft(component);
        var form = component.FindComponent<EditForm>().Instance;
        var save = form.OnValidSubmit;
        var overrideSave = Button(component, "Save Event override").Instance.OnClick;
        await experience.ReportConnectionAsync(false, null);
        var failure = new InvalidOperationException("private-provider-secret");
        service.ReadFailure = failure;
        await experience.ReportConnectionAsync(true, null);
        Assert.Empty(component.FindAll("form"));
        Assert.Same(original, Field(component.Instance, "model"));
        Assert.Contains("kept hidden", component.Find("[role=alert]").TextContent);
        Assert.DoesNotContain("private-provider-secret", component.Markup);
        Assert.Same(failure, Assert.Single(logger.Errors));
        await component.InvokeAsync(() => save.InvokeAsync(form.EditContext));
        await component.InvokeAsync(() => overrideSave.InvokeAsync());
        AssertNoMutations();
        service.ReadFailure = null;
        await ClickAsync(component, "Retry");
        Assert.Same(original, component.FindComponent<EditForm>().Instance.Model);
        Assert.Equal(.75m, original.ReminderHours);
        AssertNoMutations();
    }

    /// <summary>Transient list authorization failures never reveal cached recipient or administrator data, and explicit retry fetches current results.</summary>
    /// <param name="view">The read-only projection whose reconnect query fails unexpectedly.</param>
    /// <returns>Completion after a logged safe failure and successful fresh query.</returns>
    [Theory]
    [InlineData("inbox")]
    [InlineData("failures")]
    public async Task TransientListReauthorizationHidesUnverifiedStateUntilRetry(string view)
    {
        await experience.ReportConnectionAsync(true, null);
        if (view == "inbox")
            await VerifyTransientListAsync(Render<NotificationInbox>(), "Refresh", "old protected notification");
        else
            await VerifyTransientListAsync(Render<NotificationFailures>(), "Refresh failures", "old failure");
        AssertNoMutations();
    }

    /// <summary>Reconnect must await an already-running mutation in each notification view and cannot duplicate it while controls are busy.</summary>
    /// <param name="view">The notification page whose mutation is held by the deterministic application seam.</param>
    /// <returns>Completion after the single mutation and a subsequent current authorization read finish.</returns>
    [Theory]
    [InlineData("inbox")]
    [InlineData("failures")]
    [InlineData("preferences")]
    public async Task ReconnectWaitsForInflightMutationWithoutReplay(string view)
    {
        await experience.ReportConnectionAsync(true, null);
        service.Mutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (view == "inbox")
            await VerifyPendingMutationAsync<NotificationInbox>(() => service.ListCalls, "Mark all read", 3);
        else if (view == "failures")
            await VerifyPendingMutationAsync<NotificationFailures>(() => service.FailureCalls, "Confirm replay", 3);
        else
            await VerifyPendingMutationAsync<NotificationPreferences>(() => service.PreferenceCalls, null, 2);
        Assert.Equal(view == "inbox" ? 1 : 0, service.Marks.Count);
        Assert.Equal(view == "failures" ? 1 : 0, service.Replays.Count);
        Assert.Equal(view == "preferences" ? 1 : 0, service.SaveCalls);
    }

    /// <summary>Initial non-cooperative queries also block reconnect; the later authorization read must observe current denial rather than trust the old result.</summary>
    /// <param name="view">The page whose first query is held across loss of current access.</param>
    /// <returns>Completion after the old query releases and a distinct fresh authorization query denies access.</returns>
    [Theory]
    [InlineData("inbox")]
    [InlineData("failures")]
    [InlineData("preferences")]
    public async Task ReconnectWaitsForInflightQueryAndThenChecksCurrentAccess(string view)
    {
        await experience.ReportConnectionAsync(true, null);
        var release = BlockQuery(view);
        if (view == "inbox")
            await VerifyPendingQueryAsync<NotificationInbox>(release, () => service.ListCalls);
        else if (view == "failures")
            await VerifyPendingQueryAsync<NotificationFailures>(release, () => service.FailureCalls);
        else
            await VerifyPendingQueryAsync<NotificationPreferences>(release, () => service.PreferenceCalls);
        AssertNoMutations();
    }

    /// <summary>Repeated disposal cancels stable tokens, unsubscribes reconnect handlers, and prevents late query values or faults from reviving protected state.</summary>
    /// <param name="view">The notification page with an outstanding non-cooperative query.</param>
    /// <param name="fault">Whether the obsolete query faults instead of returning successfully.</param>
    /// <returns>Completion after disposal, late completion and a reconnect that must not query the removed page.</returns>
    [Theory]
    [InlineData("inbox", false)]
    [InlineData("inbox", true)]
    [InlineData("failures", false)]
    [InlineData("failures", true)]
    [InlineData("preferences", false)]
    [InlineData("preferences", true)]
    public async Task DisposalFencesLateQueriesAndRemovesReconnectSubscription(string view, bool fault)
    {
        await experience.ReportConnectionAsync(true, null);
        var release = BlockQuery(view, fault);
        if (view == "inbox")
            await VerifyDisposedQueryAsync(Render<NotificationInbox>(), release, "items");
        else if (view == "failures")
            await VerifyDisposedQueryAsync(Render<NotificationFailures>(), release, "failures");
        else
            await VerifyDisposedQueryAsync(Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId)), release, "model");
        Assert.Equal(0, service.CountCalls);
        Assert.Equal(0, eventQueries);
        Assert.Empty(logger.Errors);
    }

    /// <summary>Disposal during an active mutation cancels its token and fences success state, dependent reads and a waiting reconnect.</summary>
    /// <param name="view">The mutation path whose completion arrives after its view has been disposed.</param>
    /// <returns>Completion after obsolete mutation results are ignored with no dependent service reads.</returns>
    [Theory]
    [InlineData("inbox")]
    [InlineData("failures")]
    [InlineData("preferences")]
    [InlineData("event")]
    public async Task DisposalDuringMutationPreventsFollowupQueriesAndReconnect(string view)
    {
        await experience.ReportConnectionAsync(true, null);
        service.Mutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (view == "inbox")
            await VerifyDisposedMutationAsync(Render<NotificationInbox>(), "Mark all read", "items");
        else if (view == "failures")
        {
            var component = Render<NotificationFailures>();
            await ClickAsync(component, "Review replay");
            await VerifyDisposedMutationAsync(component, "Confirm replay", "failures");
        }
        else
            await VerifyDisposedMutationAsync(Render<NotificationPreferences>(p => p.Add(x => x.EventId, eventId)),
                view == "event" ? "Save Event override" : null, "model");
        Assert.Equal(1, service.ListCalls + service.FailureCalls + service.PreferenceCalls);
        Assert.Equal(view == "inbox" ? 1 : 0, service.CountCalls);
        Assert.Equal(view is "preferences" or "event" ? 1 : 0, eventQueries);
    }

    /// <summary>Two reconnect notifications arriving behind the same operation share the latest generation's serialized authorization check.</summary>
    /// <returns>Completion after both reauthorization callbacks await one current query without duplicate mutations.</returns>
    [Fact]
    public async Task OverlappingReconnectsSerializeAuthorizationQueries()
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationPreferences>();
        service.Mutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = SubmitAsync(component);
        await WaitForStartAsync(service.MutationStarted);
        await experience.ReportConnectionAsync(false, null);
        var firstReconnect = experience.ReportConnectionAsync(true, null);
        await experience.ReportConnectionAsync(false, null);
        var secondReconnect = experience.ReportConnectionAsync(true, null);
        service.Preferences = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.QueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Mutation.SetResult();
        try
        {
            await WaitForStartAsync(service.QueryStarted);
            Assert.Equal(2, service.PreferenceCalls);
            Assert.False(firstReconnect.IsCompleted);
            Assert.False(secondReconnect.IsCompleted);
        }
        finally
        {
            service.Preferences.SetResult(new(false, true, true, 1, null));
            await operation;
            await Task.WhenAll(firstReconnect, secondReconnect);
        }
        Assert.Equal(2, service.PreferenceCalls);
        Assert.Equal(1, service.SaveCalls);
        Assert.Single(component.FindAll("form"));
    }

    /// <summary>An earlier reconnect cannot complete on a query invalidated by another disconnect while the latest authorization is still pending.</summary>
    /// <param name="view">The protected notification projection whose first reconnect read becomes obsolete.</param>
    /// <returns>Completion after both connection reports have awaited current read-only authorization without replaying mutations.</returns>
    [Theory]
    [InlineData("inbox")]
    [InlineData("failures")]
    [InlineData("preferences")]
    public async Task OverlappingReconnectReportsAwaitCurrentGenerationAuthorization(string view)
    {
        await experience.ReportConnectionAsync(true, null);
        if (view == "inbox")
            await VerifyOverlappingReportsAsync(Render<NotificationInbox>(), view, () => service.ListCalls);
        else if (view == "failures")
            await VerifyOverlappingReportsAsync(Render<NotificationFailures>(), view, () => service.FailureCalls);
        else
            await VerifyOverlappingReportsAsync(Render<NotificationPreferences>(), view, () => service.PreferenceCalls);
        AssertNoMutations();
    }

    private async Task VerifyOverlappingReportsAsync<T>(IRenderedComponent<T> component, string view, Func<int> reads)
        where T : NotificationViewBase
    {
        await experience.ReportConnectionAsync(false, null);
        var releaseObsolete = BlockQuery(view);
        service.QueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReconnect = experience.ReportConnectionAsync(true, null);
        await WaitForStartAsync(service.QueryStarted);
        Assert.Equal(2, reads());
        await experience.ReportConnectionAsync(false, null);
        var secondReconnect = experience.ReportConnectionAsync(true, null);
        var releaseCurrent = BlockQuery(view);
        service.QueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseObsolete();
        try
        {
            await WaitForStartAsync(service.QueryStarted);
            await component.InvokeAsync(() => { });
            Assert.Equal(3, reads());
            Assert.False(firstReconnect.IsCompleted);
            Assert.False(secondReconnect.IsCompleted);
            Assert.Empty(component.FindAll("form"));
            Assert.DoesNotContain("old protected notification", component.Markup);
            Assert.DoesNotContain("old failure", component.Markup);
        }
        finally
        {
            releaseCurrent();
            await Task.WhenAll(firstReconnect, secondReconnect);
        }
        Assert.Equal(3, reads());
    }

    private async Task VerifyTransientListAsync<T>(IRenderedComponent<T> component, string retry, string content)
        where T : NotificationViewBase
    {
        await experience.ReportConnectionAsync(false, null);
        var failure = new InvalidOperationException("private-provider-secret");
        service.ReadFailure = failure;
        await experience.ReportConnectionAsync(true, null);
        Assert.DoesNotContain(content, component.Markup);
        Assert.DoesNotContain("private-provider-secret", component.Markup);
        Assert.Contains("could not be verified", component.Find("[role=alert]").TextContent);
        Assert.Same(failure, Assert.Single(logger.Errors));
        service.ReadFailure = null;
        await ClickAsync(component, retry);
        Assert.Contains(content, component.Markup);
        Assert.Equal(3, service.ListCalls + service.FailureCalls);
    }

    private async Task VerifyDisposedMutationAsync<T>(IRenderedComponent<T> component, string? action, string stateField)
        where T : NotificationViewBase
    {
        var form = action is null ? component.FindComponent<EditForm>().Instance : null;
        var callback = action is null ? default : Button(component, action).Instance.OnClick;
        var operation = component.InvokeAsync(() => form is null
            ? callback.InvokeAsync()
            : form.OnValidSubmit.InvokeAsync(form.EditContext));
        await WaitForStartAsync(service.MutationStarted);
        Assert.Equal(1, service.Marks.Count + service.Replays.Count + service.SaveCalls + service.EventOverrides.Count);
        await experience.ReportConnectionAsync(false, null);
        var reconnect = experience.ReportConnectionAsync(true, null);
        Assert.False(reconnect.IsCompleted);
        await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());
        Assert.All(service.Tokens, token => Assert.True(token.IsCancellationRequested));
        service.Mutation!.SetResult();
        await operation;
        await reconnect;
        Assert.Null(Field(component.Instance, stateField));
        Assert.DoesNotContain("saved", component.Find("[role=status]").TextContent);
        Assert.DoesNotContain("queued", component.Find("[role=status]").TextContent);
    }

    private async Task VerifyPendingMutationAsync<T>(Func<int> reads, string? action, int expectedReads)
        where T : NotificationViewBase
    {
        var component = Render<T>();
        if (typeof(T) == typeof(NotificationFailures))
            await ClickAsync(component, "Review replay");
        var form = action is null ? component.FindComponent<EditForm>().Instance : null;
        var callback = action is null ? default : Button(component, action).Instance.OnClick;
        Task Invoke() => form is null ? callback.InvokeAsync() : form.OnValidSubmit.InvokeAsync(form.EditContext);
        var operation = component.InvokeAsync(Invoke);
        await WaitForStartAsync(service.MutationStarted);
        Assert.Equal(1, service.Marks.Count + service.Replays.Count + service.SaveCalls);
        Assert.All(component.FindComponents<FluentButton>(), button => Assert.True(button.Instance.Disabled));
        await component.InvokeAsync(Invoke);
        await experience.ReportConnectionAsync(false, null);
        var reconnect = experience.ReportConnectionAsync(true, null);
        Assert.False(reconnect.IsCompleted);
        Assert.Equal(1, reads());
        service.Mutation!.SetResult();
        await operation;
        await reconnect;
        Assert.Equal(expectedReads, reads());
    }

    private async Task VerifyPendingQueryAsync<T>(Action release, Func<int> reads) where T : NotificationViewBase
    {
        var component = Render<T>();
        await experience.ReportConnectionAsync(false, null);
        service.Denied = true;
        var reconnect = experience.ReportConnectionAsync(true, null);
        Assert.False(reconnect.IsCompleted);
        Assert.Equal(1, reads());
        release();
        await reconnect;
        Assert.Equal(2, reads());
        Assert.Contains("no longer has access", component.Find("[role=alert]").TextContent);
        Assert.Empty(component.FindAll("form"));
        Assert.DoesNotContain("old protected notification", component.Markup);
        Assert.DoesNotContain("old failure", component.Markup);
    }

    private async Task VerifyDisposedQueryAsync<T>(IRenderedComponent<T> component, Action release, string stateField)
        where T : NotificationViewBase
    {
        await experience.ReportConnectionAsync(false, null);
        var reconnect = experience.ReportConnectionAsync(true, null);
        Assert.False(reconnect.IsCompleted);
        await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());
        await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());
        Assert.True(Assert.Single(service.Tokens).IsCancellationRequested);
        release();
        await reconnect.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False((bool)typeof(NotificationViewBase)
            .GetProperty("Busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component.Instance)!);
        Assert.Null(Field(component.Instance, stateField));
        var calls = service.ListCalls + service.FailureCalls + service.PreferenceCalls;
        await experience.ReportConnectionAsync(false, null);
        await experience.ReportConnectionAsync(true, null);
        Assert.Equal(calls, service.ListCalls + service.FailureCalls + service.PreferenceCalls);
    }

    private Action BlockQuery(string view, bool fault = false)
    {
        if (view == "inbox")
        {
            var query = new TaskCompletionSource<PageResult<NotificationSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Inbox = query;
            return () =>
            {
                if (fault) query.SetException(new InvalidOperationException("obsolete"));
                else query.SetResult(service.Items);
            };
        }
        if (view == "failures")
        {
            var query = new TaskCompletionSource<IReadOnlyList<DeliveryFailure>>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.FailureQuery = query;
            return () =>
            {
                if (fault) query.SetException(new InvalidOperationException("obsolete"));
                else query.SetResult(service.Failures);
            };
        }
        var preferences = new TaskCompletionSource<PreferenceInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Preferences = preferences;
        return () =>
        {
            if (fault) preferences.SetException(new InvalidOperationException("obsolete"));
            else preferences.SetResult(new(false, true, true, 1, null));
        };
    }

    private void AssertNoMutations()
    {
        Assert.Empty(service.Marks);
        Assert.Empty(service.Replays);
        Assert.Empty(service.EventOverrides);
        Assert.Equal(0, service.SaveCalls);
        Assert.Null(service.Saved);
    }

    private static NotificationPreferenceForm EditDraft(IRenderedComponent<NotificationPreferences> component)
    {
        var model = Assert.IsType<NotificationPreferenceForm>(component.FindComponent<EditForm>().Instance.Model);
        model.NewQuestEmail = true;
        model.ActivityEmail = false;
        model.RemindersEnabled = false;
        model.ReminderHours = .75m;
        model.TimeZoneId = "Europe/Prague";
        return model;
    }

    private static PageResult<NotificationSummary> Inbox(string text) =>
        new([new(Guid.NewGuid(), NotificationKind.QuestPublished, text, null, null, DateTimeOffset.UnixEpoch, false)], 1, 1, 25);

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    private static Task WaitForStartAsync(TaskCompletionSource started) =>
        started.Task.WaitAsync(TimeSpan.FromSeconds(1));

    private static IRenderedComponent<FluentButton> Button<T>(IRenderedComponent<T> component, string text)
        where T : ComponentBase => component.FindComponents<FluentButton>().Single(button => button.Markup.Contains($">{text}<", StringComparison.Ordinal));

    private static Task ClickAsync<T>(IRenderedComponent<T> component, string text) where T : ComponentBase =>
        component.InvokeAsync(() => Button(component, text).Instance.OnClick.InvokeAsync());

    private static Task SubmitAsync(IRenderedComponent<NotificationPreferences> component)
    {
        var form = component.FindComponent<EditForm>().Instance;
        return component.InvokeAsync(() => form.OnValidSubmit.InvokeAsync(form.EditContext));
    }

    private sealed class ErrorLogger : ILogger<NotificationViewBase>
    {
        internal List<Exception?> Errors { get; } = [];
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                Errors.Add(exception);
        }
    }
}
