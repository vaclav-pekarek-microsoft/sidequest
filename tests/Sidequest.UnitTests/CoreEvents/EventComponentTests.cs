using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Events.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.UnitTests.SecondaryExperience;
using Sidequest.Web.Components;
using Sidequest.Web.Components.Events;
using Sidequest.Web.Components.Pages.Events;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.CoreEvents;

/// <summary>Tests leaf Event rendering and validated form callbacks without routing, live providers, or browser timing.</summary>
public sealed class EventComponentTests : BunitContext
{
    /// <summary>Registers the existing Fluent rendering convention while keeping all JavaScript inside bUnit.</summary>
    public EventComponentTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static EventInput Input(string name = "Initial Event") =>
        new(name, "Private description", "Public discovery", new(2026, 7, 15), new(2026, 7, 16), "Europe/Prague");

    /// <summary>Routine reload is absent on healthy Event pages, appears after a failed read, and disappears after an explicit successful retry.</summary>
    /// <param name="pageType">Event page whose existing reload callback is exercised.</param>
    /// <returns>Completion after reconnect failure and the real rendered recovery callback.</returns>
    [Theory]
    [InlineData(typeof(EventListPage))]
    [InlineData(typeof(EventDetailPage))]
    [InlineData(typeof(EventMembersPage))]
    [InlineData(typeof(EventInvitationsPage))]
    [InlineData(typeof(EventRequestsPage))]
    [InlineData(typeof(EventEditPage))]
    [InlineData(typeof(EventBulkProgressPage))]
    public async Task EventReloadAppearsOnlyAfterFailureAndHidesAfterRecovery(Type pageType)
    {
        Services.AddLogging();
        Services.AddSingleton(TimeProvider.System);
        var experience = new ExperienceCoordinator();
        var revalidation = new EventCircuitRevalidation();
        Services.AddSingleton(experience);
        Services.AddSingleton(revalidation);
        var fail = false;
        var reads = 0;
        var id = Guid.NewGuid();
        var summary = new EventSummary(id, "Event", "Discovery", new(2026, 7, 15), new(2026, 7, 16),
            "Europe/Prague", EventStatus.Active, [], true, pageType == typeof(EventEditPage), "version");
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, _) =>
        {
            reads++;
            if (fail)
                throw new DomainException(ErrorCode.Conflict, "The item changed.");
            return method.Name switch
            {
                nameof(IEventService.ListAsync) => Task.FromResult(new PageResult<EventSummary>([summary], 1, 1, 25)),
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(summary, null)),
                nameof(IEventService.ListMembersAsync) => Task.FromResult(new PageResult<MembershipSummary>([], 0, 1, 25)),
                nameof(IEventService.ListRequestsAsync) => Task.FromResult(new PageResult<RequestSummary>([], 0, 1, 25)),
                nameof(IEventService.ListInvitationsAsync) => Task.FromResult(new PageResult<EventInvitationSummary>([], 0, 1, 25)),
                nameof(IEventService.GetBulkAsync) => Task.FromResult(new BulkOperationSummary(id, BulkMode.Add, BulkStatus.Completed, 0, 0, 0, 0, null)),
                _ => throw new NotSupportedException(method.Name)
            };
        }));
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventManagementQueries>((method, _) =>
        {
            Assert.Equal(nameof(IEventManagementQueries.ListBulkRecipientsAsync), method.Name);
            return Task.FromResult(new PageResult<BulkRecipientSummary>([], 0, 1, 25));
        }));
        await experience.ReportConnectionAsync(true, null);
        SetRendererInfo(new("Server", true));
        var cut = Render(builder =>
        {
            builder.OpenComponent(0, pageType);
            if (pageType == typeof(EventDetailPage) || pageType == typeof(EventMembersPage) ||
                pageType == typeof(EventEditPage) || pageType == typeof(EventBulkProgressPage))
                builder.AddAttribute(1, "Id", id);
            builder.CloseComponent();
        });
        IEnumerable<IRenderedComponent<FluentButton>> Reloads() => cut.FindComponents<FluentButton>()
            .Where(button => button.Find("fluent-button").TextContent.Contains("reload", StringComparison.OrdinalIgnoreCase) ||
                button.Find("fluent-button").TextContent.Contains("Refresh progress", StringComparison.Ordinal));

        Assert.Empty(cut.FindAll("[role=alert]"));
        Assert.Empty(Reloads());
        fail = true;
        await revalidation.OnConnectionUpAsync(null!, default);
        Assert.Contains("Reload and review", cut.Find("[role=alert]").TextContent);
        fail = false;
        var beforeRetry = reads;
        if (pageType == typeof(EventMembersPage))
        {
            // The route is now a thin wrapper. Its child panel owns reconnect recovery
            // and retries both its detail and audience reads on the next connection-up.
            Assert.Empty(Reloads());
            await revalidation.OnConnectionUpAsync(null!, default);
            Assert.Equal(beforeRetry + 2, reads);
        }
        else
        {
            var reload = Assert.Single(Reloads());
            Assert.False(reload.Instance.Disabled);
            await cut.InvokeAsync(() => reload.Instance.OnClick.InvokeAsync());
            Assert.Equal(beforeRetry + 1, reads);
        }
        Assert.Empty(cut.FindAll("[role=alert]"));
        Assert.Empty(Reloads());
    }

    /// <summary>Progress refresh remains useful for unfinished bulk work but is absent after terminal outcomes.</summary>
    /// <param name="status">Persisted operation status returned by the authorized query.</param>
    /// <param name="visible">Whether another progress read can track ongoing work.</param>
    /// <returns>Completion after the actual bulk progress page renders.</returns>
    [Theory]
    [InlineData(BulkStatus.Expanding, true)]
    [InlineData(BulkStatus.Applying, true)]
    [InlineData(BulkStatus.Completed, false)]
    [InlineData(BulkStatus.Failed, false)]
    public async Task BulkRefreshIsVisibleOnlyForUnfinishedWork(BulkStatus status, bool visible)
    {
        Services.AddLogging();
        var experience = new ExperienceCoordinator();
        Services.AddSingleton(experience);
        Services.AddSingleton(new EventCircuitRevalidation());
        var id = Guid.NewGuid();
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, _) =>
        {
            Assert.Equal(nameof(IEventService.GetBulkAsync), method.Name);
            return Task.FromResult(new BulkOperationSummary(id, BulkMode.Add, status, 0, 0, 0, 0, null));
        }));
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventManagementQueries>((method, _) =>
        {
            Assert.Equal(nameof(IEventManagementQueries.ListBulkRecipientsAsync), method.Name);
            return Task.FromResult(new PageResult<BulkRecipientSummary>([], 0, 1, 25));
        }));
        await experience.ReportConnectionAsync(true, null);
        SetRendererInfo(new("Server", true));
        var cut = Render<EventBulkProgressPage>(p => p.Add(x => x.Id, id));
        Assert.Equal(visible ? 1 : 0, cut.FindComponents<FluentButton>().Count(button =>
            button.Find("fluent-button").TextContent.Contains("Refresh progress", StringComparison.Ordinal)));
        Assert.Contains(status.ToString(), cut.Find("[role=status]").TextContent);
    }

    /// <summary>Busy transitions update Fluent control parameters as well as the fieldset while retaining local edits and zone locks.</summary>
    /// <returns>A task completing after disabled and re-enabled control states are explicitly verified.</returns>
    [Fact]
    public async Task EditorBusyTransitions_ExplicitlyUpdateFluentControlsWithoutReplacingInput()
    {
        var input = Input();
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, input).Add(x => x.ZoneLocked, true));
        await cut.InvokeAsync(() => cut.FindComponents<FluentTextField>().First().Instance.ValueChanged.InvokeAsync("Unsaved Event"));
        cut.Render(p => p.Add(x => x.Busy, true));
        Assert.All(cut.FindComponents<FluentTextField>(), x => Assert.True(x.Instance.Disabled));
        Assert.All(cut.FindComponents<FluentTextArea>(), x => Assert.True(x.Instance.Disabled));
        cut.Render(p => p.Add(x => x.Busy, false));
        Assert.All(cut.FindComponents<FluentTextField>(), x => Assert.False(x.Instance.Disabled));
        Assert.All(cut.FindComponents<FluentTextArea>(), x => Assert.False(x.Instance.Disabled));
        Assert.Equal("Unsaved Event", cut.FindComponents<FluentTextField>().First().Instance.Value);
        Assert.True(cut.Find("select").HasAttribute("disabled"));
        Assert.Equal("Initial Event", input.Name);
    }

    /// <summary>Renders only authorized discovery and owner contact fields, with escaped text and no member-content affordances.</summary>
    [Fact]
    public void NonmemberCardRendersDiscoveryAndNoProtectedContent()
    {
        var id = Guid.Parse("50000000-0000-0000-0000-000000000005");
        var ownerId = Guid.NewGuid();
        var item = new EventSummary(id, "<script>sentinel</script>", "Safe public discovery",
            new(2026, 7, 15), new(2026, 7, 16), "Europe/Prague", EventStatus.Active,
            [new(ownerId, "Equal owner", "owner@example.invalid")], false, false, "opaque-version");
        var cut = Render<EventCard>(p => p.Add(x => x.Item, item).Add(x => x.ShowJoinAction, true));
        Assert.Equal($"/events/{id}", cut.Find("h2 a").GetAttribute("href"));
        Assert.Equal("<script>sentinel</script>", cut.Find("h2").TextContent);
        Assert.Contains("Safe public discovery", cut.Markup);
        Assert.Contains("owner@example.invalid", cut.Markup);
        Assert.Contains("owner@example.invalid (Equal owner)", cut.Markup);
        Assert.DoesNotContain(ownerId.ToString(), cut.Markup);
        Assert.Contains("You have not joined this Event.", cut.Markup);
        Assert.Empty(cut.FindAll("script"));
        Assert.DoesNotContain("opaque-version", cut.Markup);
        Assert.DoesNotContain("You are a member", cut.Markup);
        Assert.Equal("Join Event", cut.FindComponent<FluentButton>().Find("fluent-button").TextContent.Trim());
        Assert.DoesNotContain("request membership", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("textarea, table"));
    }

    /// <summary>Formats authorized contact data without exposing the internal identifier in membership, request, invitation, or bulk labels.</summary>
    [Fact]
    public void PersonLabelUsesEmailAndDisplayNameNotAccountId()
    {
        var person = new PersonSummary(Guid.NewGuid(), "Named member", "member@example.invalid");
        Assert.Equal("member@example.invalid (Named member)", person.Label);
        Assert.DoesNotContain(person.Id.ToString(), person.Label);
    }

    /// <summary>Rejects immediately adjacent invalid name lengths without invoking the owning page callback.</summary>
    /// <param name="length">Invalid name length below or above the accepted interval.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(121)]
    public void EditorRejectsInvalidNameBoundaries(int length)
    {
        var calls = 0;
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, Input(new string('N', length)))
            .Add(x => x.Submitted, _ => calls++));
        cut.Find("form").Submit();
        Assert.Equal(0, calls);
        Assert.Contains("120", cut.Find(".validation-errors").TextContent);
        Assert.False(cut.Find("fieldset").HasAttribute("disabled"));
    }

    /// <summary>Submits edited values at both valid name boundaries, retaining dates, zone and separate private/public descriptions.</summary>
    /// <param name="length">Accepted minimum or maximum name length.</param>
    /// <returns>A task completing after renderer-dispatched binding and form submission.</returns>
    [Theory]
    [InlineData(3)]
    [InlineData(120)]
    public async Task EditorSubmitsEditedValuesAtValidBoundaries(int length)
    {
        var sent = new List<EventInput>();
        var original = Input();
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, original).Add(x => x.Submitted, value => sent.Add(value)));
        var name = new string('N', length);
        var field = cut.FindComponents<FluentTextField>().Single(x => x.Instance.Label!.StartsWith("Name", StringComparison.Ordinal));
        await cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(name));
        cut.Find("form").Submit();
        var command = Assert.Single(sent);
        Assert.Equal(name, command.Name);
        Assert.Equal("Private description", command.Description);
        Assert.Equal("Public discovery", command.DiscoverySummary);
        Assert.Equal(new DateOnly(2026, 7, 15), command.StartDate);
        Assert.Equal(new DateOnly(2026, 7, 16), command.EndDate);
        Assert.Equal("Europe/Prague", command.TimeZoneId);
        Assert.Equal("Initial Event", original.Name);
        Assert.Empty(cut.FindAll(".validation-errors"));
    }

    /// <summary>Validates independent public summary and private description maximums before invoking callbacks.</summary>
    /// <param name="summaryLength">Discovery text length.</param>
    /// <param name="descriptionLength">Private text length.</param>
    /// <param name="valid">Whether both independent limits are satisfied.</param>
    [Theory]
    [InlineData(300, 10000, true)]
    [InlineData(301, 10000, false)]
    [InlineData(300, 10001, false)]
    public void EditorEnforcesIndependentTextLimits(int summaryLength, int descriptionLength, bool valid)
    {
        var sent = new List<EventInput>();
        var input = Input() with { DiscoverySummary = new string('S', summaryLength), Description = new string('D', descriptionLength) };
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, input).Add(x => x.Submitted, value => sent.Add(value)));
        cut.Find("form").Submit();
        Assert.Equal(valid ? 1 : 0, sent.Count);
        if (valid)
        {
            Assert.Equal(summaryLength, sent[0].DiscoverySummary.Length);
            Assert.Equal(descriptionLength, sent[0].Description.Length);
        }
        else
        {
            Assert.Contains(summaryLength > 300 ? "300" : "10000", cut.Find(".validation-errors").TextContent);
        }
    }

    /// <summary>Disables save controls during work and locks the zone for published Events.</summary>
    [Fact]
    public void BusyPublishedEditorDisablesSaveAndLocksZone()
    {
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, Input()).Add(x => x.Busy, true).Add(x => x.ZoneLocked, true));
        Assert.True(cut.Find("fieldset").HasAttribute("disabled"));
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        var zone = cut.Find("select");
        Assert.True(zone.HasAttribute("disabled"));
        Assert.Equal("Europe/Prague", zone.GetAttribute("value"));
    }

    /// <summary>Blocks equal and reversed date ranges while submitting the immediately later inclusive end date.</summary>
    /// <param name="endOffsetDays">End date's whole-day offset from the start date.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void EditorRequiresEndStrictlyAfterStart(int endOffsetDays)
    {
        var input = Input();
        input = input with { EndDate = input.StartDate.AddDays(endOffsetDays) };
        var sent = new List<EventInput>();
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, input).Add(x => x.Submitted, value => sent.Add(value)));
        cut.Find("form").Submit();
        if (endOffsetDays <= 0)
        {
            Assert.Empty(sent);
            Assert.Contains("End date must be after start date.", cut.Find(".validation-errors").TextContent);
        }
        else
        {
            Assert.Equal(input, Assert.Single(sent));
            Assert.Empty(cut.FindAll(".validation-errors"));
        }
    }

    /// <summary>Offers grouped city labels backed by real TZDB identifiers and submits the selected zone unchanged.</summary>
    [Fact]
    public void EditorOffersGroupedTimeZonesAndSubmitsSelection()
    {
        var sent = new List<EventInput>();
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, Input()).Add(x => x.Submitted, value => sent.Add(value)));
        Assert.Contains("Prague", cut.Find("option[value='Europe/Prague']").TextContent);
        Assert.Contains("Budapest", cut.Find("option[value='Europe/Prague']").TextContent);
        Assert.StartsWith("(UTC+02:00)", cut.Find("option[value='Europe/Prague']").TextContent);
        Assert.StartsWith("(UTC-04:00)", cut.Find("option[value='America/New_York']").TextContent);
        Assert.False(cut.Find("select").HasAttribute("disabled"));
        cut.Find("select").Change("America/New_York");
        cut.Find("form").Submit();
        Assert.Equal("America/New_York", Assert.Single(sent).TimeZoneId);
    }

    /// <summary>Changing the start date refreshes daylight-saving labels without changing the retained Event zone.</summary>
    [Fact]
    public void EditorUpdatesZoneOffsetsWhenStartDateChanges()
    {
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, Input()));
        cut.Find("input[name='event-start-date']").Change("2026-01-15");
        Assert.StartsWith("(UTC+01:00)", cut.Find("option[value='Europe/Prague']").TextContent);
        Assert.StartsWith("(UTC-05:00)", cut.Find("option[value='America/New_York']").TextContent);
        Assert.Equal("Europe/Prague", cut.Find("select").GetAttribute("value"));
    }
    /// <summary>Rejects a tampered unknown zone before invoking the parent command callback.</summary>
    [Fact]
    public void EditorRejectsUnknownTimeZone()
    {
        var calls = 0;
        var cut = Render<EventEditor>(p => p.Add(x => x.Input, Input() with { TimeZoneId = "Unknown/Zone" })
            .Add(x => x.Submitted, _ => calls++));
        cut.Find("form").Submit();
        Assert.Equal(0, calls);
        Assert.Contains("Choose an available IANA time zone.", cut.Find(".validation-errors").TextContent);
    }

    /// <summary>Cancellation reviews impact in the shared dialog, retains the entered reason, and sends the exact command only after confirmation.</summary>
    /// <returns>Completion after checking the dialog gate and the lifecycle command's Event, version, target, and reason.</returns>
    [Fact]
    public async Task EventLifecycleCancelUsesConfirmActionDialogAndMutatesOnlyAfterConfirm()
    {
        Services.AddLogging();
        var experience = new ExperienceCoordinator();
        Services.AddSingleton(experience);
        Services.AddSingleton(new EventCircuitRevalidation());
        var impactReads = 0;
        var commands = new List<(Guid Id, string Version, EventStatus Status, string Reason)>();
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventManagementQueries>((method, _) =>
        {
            Assert.Equal(nameof(IEventManagementQueries.GetCancellationImpactAsync), method.Name);
            impactReads++;
            return Task.FromResult(3);
        }));
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.ChangeStatusAsync), method.Name);
            commands.Add(((Guid)arguments![0]!, (string)arguments[1]!, (EventStatus)arguments[2]!, (string)arguments[3]!));
            return Task.CompletedTask;
        }));
        await experience.ReportConnectionAsync(true, null);
        SetRendererInfo(new("Server", true));
        var item = new EventSummary(Guid.NewGuid(), "Event", "", new(2026, 7, 15), new(2026, 7, 16),
            "Europe/Prague", EventStatus.Active, [], true, true, "original-version");
        var cut = Render<EventLifecycle>(p => p.Add(x => x.Item, item));
        Task ClickAsync(string label) => cut.InvokeAsync(() => cut.FindComponents<FluentButton>()
            .Single(x => x.Find("fluent-button").TextContent.Trim() == label).Instance.OnClick.InvokeAsync());

        Assert.Empty(cut.FindComponents<FluentCheckbox>());
        Assert.Equal(0, impactReads);
        Assert.False(cut.FindComponent<ConfirmActionDialog>().Instance.Open);
        await ClickAsync("Cancel Event…");
        var dialog = cut.FindComponent<ConfirmActionDialog>();
        Assert.True(dialog.Instance.Open);
        Assert.Equal("Cancel Event", dialog.Instance.Title);
        Assert.Equal("Cancel Event and affected Quests", dialog.Instance.ConfirmLabel);
        Assert.Equal(1, impactReads);
        Assert.Contains("3 Draft, Active, or Suspended child Quests", dialog.Markup);
        Assert.Empty(commands);
        const string reason = "Retain this cancellation explanation.";
        await cut.InvokeAsync(() => dialog.FindComponent<FluentTextArea>().Instance.ValueChanged.InvokeAsync(reason));
        Assert.Equal(reason, dialog.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Empty(commands);
        await ClickAsync("Cancel Event and affected Quests");
        Assert.Equal((item.Id, item.Version, EventStatus.Cancelled, reason), Assert.Single(commands));
        Assert.False(cut.FindComponent<ConfirmActionDialog>().Instance.Open);
    }

    /// <summary>Shows explicit pending and safe failure feedback and clears both when the next operation succeeds.</summary>
    [Fact]
    public void FeedbackShowsAndClearsLoadingAndFailureStates()
    {
        var cut = Render<EventFeedback>(p => p.Add(x => x.Busy, true).Add(x => x.Error, "<unsafe> failed"));
        Assert.Equal("Loading or saving…", cut.Find("[role=status]").TextContent);
        Assert.Equal("<unsafe> failed", cut.Find("[role=alert]").TextContent);
        Assert.Empty(cut.FindAll("unsafe"));
        cut.Render(p => p.Add(x => x.Busy, false).Add(x => x.Error, (string?)null));
        Assert.Empty(cut.FindAll("[role=status], [role=alert]"));
    }

    /// <summary>The shared confirmation surface is non-actionable while closed and becomes a modal, focus-trapping dialog when opened.</summary>
    [Fact]
    public void ConfirmActionDialogIsHiddenUntilShownThenRendersModalWithFocusTrap()
    {
        var cut = Render<ConfirmActionDialog>(parameters => parameters
            .Add(component => component.Title, "Delete Event")
            .Add(component => component.ConfirmLabel, "Delete Event"));

        var dialog = cut.FindComponent<FluentDialog>();
        Assert.True(dialog.Instance.Hidden);
        Assert.True(dialog.Instance.Modal);
        Assert.True(dialog.Instance.TrapFocus);

        cut.Render(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Title, "Delete Event")
            .Add(component => component.ConfirmLabel, "Delete Event"));

        dialog = cut.FindComponent<FluentDialog>();
        Assert.False(dialog.Instance.Hidden);
        Assert.True(dialog.Instance.Modal);
        Assert.True(dialog.Instance.TrapFocus);
        Assert.Equal("Delete Event", dialog.Find("h2").TextContent);
    }

    /// <summary>Confirmation is delivered once when enabled, while busy host state disables both commands and suppresses further callbacks.</summary>
    /// <returns>Completion after renderer-dispatched command callbacks and disabled clicks are observed.</returns>
    [Fact]
    public async Task ConfirmActionDialogConfirmInvokesOnceAndBusyStateSuppressesCommands()
    {
        var confirmed = 0;
        var dismissed = 0;
        var cut = Render<ConfirmActionDialog>(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Title, "Cancel Event")
            .Add(component => component.ConfirmLabel, "Cancel Event")
            .Add(component => component.Confirmed, () => confirmed++)
            .Add(component => component.Dismissed, () => dismissed++));

        var buttons = cut.FindComponents<FluentButton>();
        await cut.InvokeAsync(() => buttons.Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Cancel Event").Instance.OnClick.InvokeAsync());
        Assert.Equal(1, confirmed);
        Assert.Equal(0, dismissed);

        cut.Render(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Title, "Cancel Event")
            .Add(component => component.ConfirmLabel, "Cancel Event")
            .Add(component => component.Busy, true)
            .Add(component => component.ConfirmDisabled, true)
            .Add(component => component.Confirmed, () => confirmed++)
            .Add(component => component.Dismissed, () => dismissed++));

        buttons = cut.FindComponents<FluentButton>();
        Assert.All(buttons, button => Assert.True(button.Instance.Disabled));
        foreach (var button in cut.FindAll("fluent-button"))
            button.Click();
        Assert.Equal(1, confirmed);
        Assert.Equal(0, dismissed);
    }

    /// <summary>Dismissal closes through its dedicated callback without accidentally confirming the destructive action.</summary>
    /// <returns>Completion after the rendered dismissal command is invoked.</returns>
    [Fact]
    public async Task ConfirmActionDialogDismissInvokesDismissWithoutConfirm()
    {
        var confirmed = 0;
        var dismissed = 0;
        var cut = Render<ConfirmActionDialog>(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Title, "Remove member")
            .Add(component => component.ConfirmLabel, "Remove member")
            .Add(component => component.Confirmed, () => confirmed++)
            .Add(component => component.Dismissed, () => dismissed++));

        var dismiss = cut.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Keep current state");
        await cut.InvokeAsync(() => dismiss.Instance.OnClick.InvokeAsync());

        Assert.Equal(1, dismissed);
        Assert.Equal(0, confirmed);
    }

    /// <summary>A member who is not an owner sees exactly the three general Event-management tabs in stable order.</summary>
    [Fact]
    public void EventManagementTabsNonOwnerRendersExactlyOverviewQuestsAndMembers()
    {
        var cut = Render<EventManagementTabs>();

        Assert.Equal(["Overview", "Quests", "Members"],
            cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
        Assert.DoesNotContain("Invitations", cut.Markup);
        Assert.DoesNotContain("Settings", cut.Markup);
    }

    /// <summary>An owner sees the three general tabs followed by exactly Invitations and Settings.</summary>
    [Fact]
    public void EventManagementTabsOwnerRendersOverviewQuestsMembersInvitationsAndSettings()
    {
        var cut = Render<EventManagementTabs>(parameters => parameters.Add(component => component.IsOwner, true));

        Assert.Equal(["Overview", "Quests", "Members", "Invitations", "Settings"],
            cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
    }

    /// <summary>Selecting either a general or owner-only tab emits its stable identifier exactly once.</summary>
    /// <param name="isOwner">Whether owner-only tabs are rendered.</param>
    /// <param name="label">Visible tab label selected by the member.</param>
    /// <param name="expected">Stable identifier delivered to the owning page.</param>
    [Theory]
    [InlineData(false, "Quests", "quests")]
    [InlineData(true, "Settings", "settings")]
    public void EventManagementTabsSelectionInvokesChangedOnceWithSelectedTab(bool isOwner, string label, string expected)
    {
        var selected = new List<string>();
        var cut = Render<EventManagementTabs>(parameters => parameters
            .Add(component => component.IsOwner, isOwner)
            .Add(component => component.SelectedChanged, value => selected.Add(value)));

        cut.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == label).Click();

        Assert.Equal(expected, Assert.Single(selected));
    }

    /// <summary>An Active nonmember receives only the direct Join action, which identifies its card exactly once.</summary>
    [Fact]
    public async Task EventCardActiveNonmemberRendersDirectJoinAndInvokesCardIdOnce()
    {
        var id = Guid.Parse("51000000-0000-0000-0000-000000000015");
        var joined = new List<Guid>();
        var item = new EventSummary(id, "Joinable Event", "Public discovery",
            new(2026, 8, 10), new(2026, 8, 12), "Europe/Prague", EventStatus.Active,
            [], false, false, "join-version");
        var cut = Render<EventCard>(parameters => parameters
            .Add(component => component.Item, item)
            .Add(component => component.ShowJoinAction, true)
            .Add(component => component.JoinRequested, value => joined.Add(value)));

        var join = Assert.Single(cut.FindComponents<FluentButton>());
        Assert.Equal("Join Event", join.Find("fluent-button").TextContent.Trim());
        Assert.DoesNotContain("request membership", cut.Markup, StringComparison.OrdinalIgnoreCase);
        await cut.InvokeAsync(() => join.Instance.OnClick.InvokeAsync());

        Assert.Equal(id, Assert.Single(joined));
    }

    /// <summary>Joined Active Events and every non-Active lifecycle partition omit the direct Join action.</summary>
    /// <param name="status">Lifecycle status represented by the card.</param>
    /// <param name="isMember">Whether the viewer already joined the Active Event.</param>
    [Theory]
    [InlineData(EventStatus.Active, true)]
    [InlineData(EventStatus.Draft, false)]
    [InlineData(EventStatus.Completed, false)]
    [InlineData(EventStatus.Cancelled, false)]
    [InlineData(EventStatus.Archived, false)]
    public void EventCardJoinedDraftAndTerminalPartitionsDoNotRenderJoin(EventStatus status, bool isMember)
    {
        var item = new EventSummary(Guid.NewGuid(), $"{status} Event", "Public discovery",
            new(2026, 8, 10), new(2026, 8, 12), "Europe/Prague", status,
            [], isMember, false, "partition-version");
        var cut = Render<EventCard>(parameters => parameters
            .Add(component => component.Item, item)
            .Add(component => component.ShowJoinAction, true));

        Assert.Empty(cut.FindComponents<FluentButton>());
        Assert.Contains(status.ToString(), cut.Markup);
        Assert.DoesNotContain("request membership", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The editor renders an explicitly supplied create label while preserving its exact default edit label.</summary>
    /// <param name="submitLabel">Optional label supplied by a create host.</param>
    /// <param name="expected">Exact submit label rendered by the editor.</param>
    [Theory]
    [InlineData("Create Event", "Create Event")]
    [InlineData(null, "Save changes")]
    public void EventEditorRendersProvidedSubmitLabel(string? submitLabel, string expected)
    {
        var cut = Render<EventEditor>(parameters =>
        {
            parameters.Add(component => component.Input, Input());
            if (submitLabel is not null)
                parameters.Add(component => component.SubmitLabel, submitLabel);
        });

        var submit = Assert.Single(cut.FindComponents<FluentButton>());
        Assert.Equal(expected, submit.Find("fluent-button").TextContent.Trim());
        Assert.Equal(ButtonType.Submit, submit.Instance.Type);
    }

    /// <summary>A member can browse and create Quests scoped to the Event without receiving owner moderation.</summary>
    [Fact]
    public void EventQuestsPanelMemberRendersQuestLinksWithoutOwnerModeration()
    {
        var eventId = Guid.Parse("52000000-0000-0000-0000-000000000025");
        var cut = Render<EventQuestsPanel>(parameters => parameters
            .Add(component => component.EventId, eventId)
            .Add(component => component.CanCreate, true));
        var links = cut.FindAll("a");

        Assert.Equal(["Browse Event Quests", "Create Quest"], links.Select(link => link.TextContent.Trim()));
        Assert.Equal($"/quests?view=Discover&eventId={eventId}", links[0].GetAttribute("href"));
        Assert.Equal($"/quests/create?eventId={eventId}", links[1].GetAttribute("href"));
        Assert.DoesNotContain("Moderate Quests", cut.Markup);
    }

    /// <summary>An owner retains member Quest destinations and additionally receives the Event-scoped moderation destination.</summary>
    [Fact]
    public void EventQuestsPanelOwnerAdditionallyRendersModerationLink()
    {
        var eventId = Guid.Parse("53000000-0000-0000-0000-000000000035");
        var cut = Render<EventQuestsPanel>(parameters => parameters
            .Add(component => component.EventId, eventId)
            .Add(component => component.CanCreate, true)
            .Add(component => component.CanModerate, true));
        var links = cut.FindAll("a");

        Assert.Equal(["Browse Event Quests", "Create Quest", "Moderate Quests"],
            links.Select(link => link.TextContent.Trim()));
        Assert.Equal($"/quests?view=Discover&eventId={eventId}", links[0].GetAttribute("href"));
        Assert.Equal($"/quests/create?eventId={eventId}", links[1].GetAttribute("href"));
        Assert.Equal($"/quests?view=Moderation&eventId={eventId}", links[2].GetAttribute("href"));
    }

    /// <summary>The Event list requests All by default and offers Join only on an Active card the actor has not joined.</summary>
    /// <returns>Completion after the interactive page performs its initial authorized read.</returns>
    [Fact]
    public async Task EventListPageDefaultsToAllAndRendersJoinOnlyForActiveNonmembers()
    {
        var requests = new List<(EventListKind Kind, int Page)>();
        var activeJoined = Summary(Guid.Parse("60000000-0000-0000-0000-000000000001"), "Active joined", EventStatus.Active, true);
        var activeUnjoined = Summary(Guid.Parse("60000000-0000-0000-0000-000000000002"), "Active unjoined", EventStatus.Active, false);
        var draft = Summary(Guid.Parse("60000000-0000-0000-0000-000000000003"), "Legacy draft", EventStatus.Draft, false);
        var completed = Summary(Guid.Parse("60000000-0000-0000-0000-000000000004"), "Completed", EventStatus.Completed, false);
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.ListAsync), method.Name);
            var request = (PageRequest)arguments![1]!;
            requests.Add(((EventListKind)arguments[0]!, request.Page));
            return Task.FromResult(new PageResult<EventSummary>(
                [activeJoined, activeUnjoined, draft, completed], 4, request.Page, request.PageSize));
        }));

        var cut = Render<EventListPage>();

        Assert.Equal((EventListKind.All, 1), Assert.Single(requests));
        Assert.Equal(4, cut.FindComponents<EventCard>().Count);
        var join = Assert.Single(cut.FindComponents<FluentButton>(),
            button => button.Find("fluent-button").TextContent.Trim() == "Join Event");
        Assert.Single(cut.FindComponents<EventCard>().Single(card => card.Instance.Item.Id == activeUnjoined.Id)
            .FindComponents<FluentButton>());
        Assert.False(join.Instance.Disabled);
        Assert.DoesNotContain("Join Event", cut.FindComponents<EventCard>()
            .Single(card => card.Instance.Item.Id == activeJoined.Id).Markup);
        Assert.DoesNotContain("Join Event", cut.FindComponents<EventCard>()
            .Single(card => card.Instance.Item.Id == draft.Id).Markup);
        Assert.DoesNotContain("Join Event", cut.FindComponents<EventCard>()
            .Single(card => card.Instance.Item.Id == completed.Id).Markup);
    }

    /// <summary>Joining from page two sends the selected card identifier once and reloads the same All page into joined state.</summary>
    /// <returns>Completion after paging, joining, and the resulting authorized list refresh.</returns>
    [Fact]
    public async Task EventListPageJoinCallsServiceOnceWithCardIdAndReloadsCurrentPage()
    {
        var eventId = Guid.Parse("61000000-0000-0000-0000-000000000011");
        var listRequests = new List<(EventListKind Kind, int Page)>();
        var joins = new List<Guid>();
        var joined = false;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            if (method.Name == nameof(IEventService.JoinAsync))
            {
                joins.Add((Guid)arguments![0]!);
                joined = true;
                return Task.CompletedTask;
            }

            Assert.Equal(nameof(IEventService.ListAsync), method.Name);
            var request = (PageRequest)arguments![1]!;
            listRequests.Add(((EventListKind)arguments[0]!, request.Page));
            IReadOnlyList<EventSummary> items = request.Page == 2
                ? [Summary(eventId, "Page two Event", EventStatus.Active, joined)]
                : [Summary(Guid.Parse("61000000-0000-0000-0000-000000000012"), "Page one Event", EventStatus.Active, true)];
            return Task.FromResult(new PageResult<EventSummary>(items, 26, request.Page, request.PageSize));
        }));
        var cut = Render<EventListPage>();

        var next = cut.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Next");
        await cut.InvokeAsync(() => next.Instance.OnClick.InvokeAsync());
        var join = cut.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Join Event");
        await cut.InvokeAsync(() => join.Instance.OnClick.InvokeAsync());

        Assert.Equal(eventId, Assert.Single(joins));
        Assert.Equal(
            [(EventListKind.All, 1), (EventListKind.All, 2), (EventListKind.All, 2)],
            listRequests);
        Assert.Equal("Page 2 · 26 items", cut.Find("nav[aria-label='List pages'] span").TextContent.Trim());
        Assert.DoesNotContain(cut.FindComponents<FluentButton>(),
            button => button.Find("fluent-button").TextContent.Trim() == "Join Event");
        Assert.Contains("You joined this Event.", cut.Markup);
    }

    /// <summary>The Event list exposes direct Join without retaining any request-membership action surface.</summary>
    /// <returns>Completion after the All view renders an eligible discovery card.</returns>
    [Fact]
    public async Task EventListPageContainsNoRequestMembershipAction()
    {
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.ListAsync), method.Name);
            var request = (PageRequest)arguments![1]!;
            return Task.FromResult(new PageResult<EventSummary>(
                [Summary(Guid.Parse("62000000-0000-0000-0000-000000000021"), "Discoverable", EventStatus.Active, false)],
                1, request.Page, request.PageSize));
        }));

        var cut = Render<EventListPage>();

        Assert.Single(cut.FindComponents<FluentButton>(),
            button => button.Find("fluent-button").TextContent.Trim() == "Join Event");
        Assert.DoesNotContain("request membership", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cut.FindAll("a, button, fluent-button"), element =>
            element.TextContent.Contains("request", StringComparison.OrdinalIgnoreCase) &&
            element.TextContent.Contains("membership", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A discovery-only visitor joins directly with the route identifier and reloads into the three protected member tabs.</summary>
    /// <returns>Completion after direct Join and the immediate authorized detail reload.</returns>
    [Fact]
    public async Task EventDetailPageNonmemberJoinsDirectlyThenReloadsIntoMemberTabs()
    {
        var eventId = Guid.Parse("63000000-0000-0000-0000-000000000031");
        var joins = new List<Guid>();
        var detailReads = 0;
        var joined = false;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            if (method.Name == nameof(IEventService.JoinAsync))
            {
                joins.Add((Guid)arguments![0]!);
                joined = true;
                return Task.CompletedTask;
            }

            Assert.Equal(nameof(IEventService.GetAsync), method.Name);
            Assert.Equal(eventId, (Guid)arguments![0]!);
            detailReads++;
            var summary = Summary(eventId, "Direct Join Event", EventStatus.Active, joined);
            return Task.FromResult(new EventDetail(summary, joined ? "Protected member description" : null));
        }));
        var cut = Render<EventDetailPage>(parameters => parameters.Add(component => component.Id, eventId));

        Assert.Empty(cut.FindAll("[role=tab]"));
        Assert.Contains("No owner approval is required.", cut.Markup);
        var join = cut.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Join Event");
        await cut.InvokeAsync(() => join.Instance.OnClick.InvokeAsync());

        Assert.Equal(eventId, Assert.Single(joins));
        Assert.Equal(2, detailReads);
        Assert.Equal(["Overview", "Quests", "Members"],
            cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
        Assert.Contains("Protected member description", cut.Markup);
        Assert.DoesNotContain(cut.FindComponents<FluentButton>(),
            button => button.Find("fluent-button").TextContent.Trim() == "Join Event");
    }

    /// <summary>A non-owner member receives exactly the general tabs and their real Event-scoped Quest and member contents.</summary>
    /// <returns>Completion after selecting both non-overview protected tabs.</returns>
    [Fact]
    public async Task EventDetailPageNonOwnerRendersExactlyOverviewQuestsAndMembers()
    {
        var eventId = Guid.Parse("64000000-0000-0000-0000-000000000041");
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(eventId, (Guid)arguments![0]!);
            return method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(
                    new EventDetail(Summary(eventId, "Member Event", EventStatus.Active, true), "Member description")),
                nameof(IEventService.ListMembersAsync) => Task.FromResult(
                    new PageResult<MembershipSummary>([], 0, ((PageRequest)arguments[1]!).Page, 25)),
                _ => throw new NotSupportedException(method.Name)
            };
        }));
        var cut = Render<EventDetailPage>(parameters => parameters.Add(component => component.Id, eventId));

        Assert.Equal(["Overview", "Quests", "Members"],
            cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
        Assert.DoesNotContain("Invitations", cut.Markup);
        Assert.DoesNotContain("Settings", cut.Markup);
        cut.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == "Quests").Click();
        var quests = cut.FindComponent<EventQuestsPanel>();
        Assert.Equal(eventId, quests.Instance.EventId);
        Assert.False(quests.Instance.CanModerate);
        Assert.Equal($"/quests?view=Discover&eventId={eventId}", quests.Find("a").GetAttribute("href"));
        cut.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == "Members").Click();
        Assert.Equal(eventId, cut.FindComponent<EventMembersPanel>().Instance.EventId);
        Assert.Contains("No individual membership records in this view.", cut.Markup);
    }

    /// <summary>An owner receives the exact five-tab set, with real invitation content and editor/lifecycle settings composition.</summary>
    /// <returns>Completion after rendering both owner-only tab contents.</returns>
    [Fact]
    public async Task EventDetailPageOwnerRendersInvitationsAndSettingsInAdditionToMemberTabs()
    {
        var eventId = Guid.Parse("65000000-0000-0000-0000-000000000051");
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(eventId, (Guid)arguments![0]!);
            return method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(
                    Summary(eventId, "Owner Event", EventStatus.Active, true, true), "Owner-only description")),
                nameof(IEventService.ListInvitationsAsync) => Task.FromResult(
                    new PageResult<EventInvitationSummary>([], 0, ((PageRequest)arguments[1]!).Page, 25)),
                _ => throw new NotSupportedException(method.Name)
            };
        }));
        var cut = Render<EventDetailPage>(parameters => parameters.Add(component => component.Id, eventId));

        Assert.Equal(["Overview", "Quests", "Members", "Invitations", "Settings"],
            cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
        cut.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == "Invitations").Click();
        Assert.Equal(eventId, cut.FindComponent<EventInvitationsPanel>().Instance.EventId);
        Assert.Contains("No Event invitations.", cut.Markup);
        cut.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == "Settings").Click();
        var settings = cut.FindComponent<EventSettingsPanel>();
        Assert.Equal(eventId, settings.Instance.Detail.Summary.Id);
        Assert.Equal("Owner-only description", settings.Instance.Detail.Description);
        Assert.Single(settings.FindComponents<EventEditor>());
        Assert.Single(settings.FindComponents<EventLifecycle>());
        Assert.Contains("Save changes", settings.Markup);
        Assert.Contains("Event actions", settings.Markup);
    }

    /// <summary>Losing ownership while Settings is selected resets the detail page to Overview and removes every owner-only surface.</summary>
    /// <returns>Completion after the route reloads the downgraded member projection.</returns>
    [Fact]
    public async Task EventDetailPageOwnerDowngradeWhileOwnerTabSelectedReturnsToOverview()
    {
        var eventId = Guid.Parse("66000000-0000-0000-0000-000000000061");
        var isOwner = true;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.GetAsync), method.Name);
            Assert.Equal(eventId, (Guid)arguments![0]!);
            return Task.FromResult(new EventDetail(
                Summary(eventId, "Ownership changed", EventStatus.Active, true, isOwner), "Current description"));
        }));
        var cut = Render<EventDetailPage>(parameters => parameters.Add(component => component.Id, eventId));
        cut.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == "Settings").Click();
        Assert.Single(cut.FindComponents<EventSettingsPanel>());

        isOwner = false;
        cut.Render(parameters => parameters.Add(component => component.Id, eventId));

        Assert.Equal(["Overview", "Quests", "Members"],
            cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
        Assert.Single(cut.FindAll("h2"), heading => heading.TextContent.Trim() == "About this Event");
        Assert.Empty(cut.FindComponents<EventSettingsPanel>());
        Assert.DoesNotContain("Invitations", cut.Markup);
        Assert.DoesNotContain("Settings", cut.Markup);
    }

    /// <summary>The detail surface contains no request-membership action before or after its direct Join transition.</summary>
    /// <returns>Completion after checking both discovery and protected member states.</returns>
    [Fact]
    public async Task EventDetailPageContainsNoRequestMembershipAction()
    {
        var eventId = Guid.Parse("67000000-0000-0000-0000-000000000071");
        var joined = false;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(eventId, (Guid)arguments![0]!);
            if (method.Name == nameof(IEventService.JoinAsync))
            {
                joined = true;
                return Task.CompletedTask;
            }
            Assert.Equal(nameof(IEventService.GetAsync), method.Name);
            return Task.FromResult(new EventDetail(
                Summary(eventId, "No request flow", EventStatus.Active, joined), joined ? "Member content" : null));
        }));
        var cut = Render<EventDetailPage>(parameters => parameters.Add(component => component.Id, eventId));

        Assert.DoesNotContain("request membership", cut.Markup, StringComparison.OrdinalIgnoreCase);
        var join = cut.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Join Event");
        await cut.InvokeAsync(() => join.Instance.OnClick.InvokeAsync());
        Assert.DoesNotContain("request membership", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cut.FindAll("a, button, fluent-button"), element =>
            element.TextContent.Contains("request", StringComparison.OrdinalIgnoreCase) &&
            element.TextContent.Contains("membership", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The thin members route forwards its Event identifier to one real members panel that performs the authorized reads.</summary>
    /// <returns>Completion after the composed members panel loads its detail and audience snapshots.</returns>
    [Fact]
    public async Task EventMembersPageComposesMembersPanelForRouteEventId()
    {
        var eventId = Guid.Parse("68000000-0000-0000-0000-000000000081");
        var calls = new List<string>();
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(eventId, (Guid)arguments![0]!);
            calls.Add(method.Name);
            return method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(
                    Summary(eventId, "Members route", EventStatus.Active, true), "Member content")),
                nameof(IEventService.ListMembersAsync) => Task.FromResult(
                    new PageResult<MembershipSummary>([], 0, ((PageRequest)arguments[1]!).Page, 25)),
                _ => throw new NotSupportedException(method.Name)
            };
        }));

        var cut = Render<EventMembersPage>(parameters => parameters.Add(component => component.Id, eventId));

        var panel = Assert.Single(cut.FindComponents<EventMembersPanel>());
        Assert.Equal(eventId, panel.Instance.EventId);
        Assert.Equal([nameof(IEventService.GetAsync), nameof(IEventService.ListMembersAsync)], calls);
        Assert.Equal($"/events/{eventId}", cut.Find("nav[aria-label='Membership actions'] a").GetAttribute("href"));
    }

    /// <summary>Deleting a legacy Draft opens the shared dialog, while dismissal preserves it and confirmation sends the exact versioned command.</summary>
    /// <returns>Completion after exercising both dismissal and confirmation through the rendered dialog.</returns>
    [Fact]
    public async Task EventLifecycleDeleteUsesConfirmActionDialogAndDismissDoesNotMutate()
    {
        Services.AddLogging();
        var experience = new ExperienceCoordinator();
        Services.AddSingleton(experience);
        Services.AddSingleton(new EventCircuitRevalidation());
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventManagementQueries>((method, _) =>
            throw new NotSupportedException(method.Name)));
        var deletions = new List<(Guid Id, string Version)>();
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.DeleteDraftAsync), method.Name);
            deletions.Add(((Guid)arguments![0]!, (string)arguments[1]!));
            return Task.CompletedTask;
        }));
        await experience.ReportConnectionAsync(true, null);
        SetRendererInfo(new("Server", true));
        var item = Summary(Guid.Parse("69000000-0000-0000-0000-000000000091"),
            "Legacy Draft", EventStatus.Draft, true, true);
        var cut = Render<EventLifecycle>(parameters => parameters.Add(component => component.Item, item));

        await ClickButtonAsync(cut, "Delete legacy Draft…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
            candidate => candidate.Instance.Open && candidate.Instance.Title == "Delete legacy Draft Event");
        Assert.Equal("Delete Draft permanently", dialog.Instance.ConfirmLabel);
        Assert.Empty(deletions);
        await ClickButtonAsync(dialog, "Keep current state");
        Assert.Empty(deletions);
        Assert.False(dialog.Instance.Open);

        await ClickButtonAsync(cut, "Delete legacy Draft…");
        Assert.Empty(deletions);
        await ClickButtonAsync(cut.FindComponents<ConfirmActionDialog>().Single(candidate => candidate.Instance.Open),
            "Delete Draft permanently");

        Assert.Equal((item.Id, item.Version), Assert.Single(deletions));
    }

    /// <summary>Lifecycle cancellation and deletion use dialogs without introducing any checkbox-based destructive confirmation.</summary>
    [Fact]
    public void EventLifecycleDestructiveActionsRenderNoInlineConfirmationCheckbox()
    {
        Services.AddLogging();
        Services.AddSingleton(new ExperienceCoordinator());
        Services.AddSingleton(new EventCircuitRevalidation());
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, _) =>
            throw new NotSupportedException(method.Name)));
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventManagementQueries>((method, _) =>
            throw new NotSupportedException(method.Name)));
        SetRendererInfo(new("Server", true));
        var draft = Summary(Guid.Parse("6a000000-0000-0000-0000-0000000000a1"),
            "Legacy Draft", EventStatus.Draft, true, true);
        var cut = Render<EventLifecycle>(parameters => parameters.Add(component => component.Item, draft));

        Assert.Equal(2, cut.FindComponents<ConfirmActionDialog>().Count);
        Assert.Empty(cut.FindComponents<FluentCheckbox>());
        Assert.Empty(cut.FindComponents<ConfirmActionDialog>()
            .SelectMany(dialog => dialog.FindComponents<FluentCheckbox>()));
    }

    /// <summary>A non-owner's leave command is held behind the shared dialog and receives the route Event identifier exactly once.</summary>
    /// <returns>Completion after confirming the rendered leave dialog.</returns>
    [Fact]
    public async Task EventDetailPageLeaveUsesConfirmActionDialogAndMutatesOnlyAfterConfirm()
    {
        var eventId = Guid.Parse("6b000000-0000-0000-0000-0000000000b1");
        var leaves = new List<Guid>();
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            if (method.Name == nameof(IEventService.LeaveAsync))
            {
                leaves.Add((Guid)arguments![0]!);
                return Task.CompletedTask;
            }

            Assert.Equal(nameof(IEventService.GetAsync), method.Name);
            return Task.FromResult(new EventDetail(
                Summary(eventId, "Leave Event", EventStatus.Active, true), "Member details"));
        }));
        var cut = Render<EventDetailPage>(parameters => parameters.Add(component => component.Id, eventId));

        await ClickButtonAsync(cut, "Leave Event…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
            candidate => candidate.Instance.Open);
        Assert.Equal("Leave Event", dialog.Instance.Title);
        Assert.Equal("Leave Event", dialog.Instance.ConfirmLabel);
        Assert.Contains("Quest invitations, attendance, follows", dialog.Markup);
        Assert.Empty(leaves);
        await ClickButtonAsync(dialog, "Leave Event");

        Assert.Equal(eventId, Assert.Single(leaves));
    }

    /// <summary>Dismissing leave preserves membership, and its destructive dialog contains no checkbox confirmation control.</summary>
    /// <returns>Completion after dismissing the rendered leave dialog.</returns>
    [Fact]
    public async Task EventDetailPageLeaveDismissesWithoutMutationAndHasNoDestructiveCheckbox()
    {
        var eventId = Guid.Parse("6c000000-0000-0000-0000-0000000000c1");
        var leaveCalls = 0;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, _) =>
        {
            if (method.Name == nameof(IEventService.LeaveAsync))
            {
                leaveCalls++;
                return Task.CompletedTask;
            }

            Assert.Equal(nameof(IEventService.GetAsync), method.Name);
            return Task.FromResult(new EventDetail(
                Summary(eventId, "Stay Event", EventStatus.Active, true), "Member details"));
        }));
        var cut = Render<EventDetailPage>(parameters => parameters.Add(component => component.Id, eventId));

        await ClickButtonAsync(cut, "Leave Event…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
            candidate => candidate.Instance.Open);
        Assert.Empty(dialog.FindComponents<FluentCheckbox>());
        Assert.Empty(cut.FindComponents<FluentCheckbox>());
        await ClickButtonAsync(dialog, "Keep current state");

        Assert.Equal(0, leaveCalls);
        Assert.False(dialog.Instance.Open);
    }

    /// <summary>Member removal binds the selected person to the shared dialog and sends the exact reasoned command only after confirmation.</summary>
    /// <returns>Completion after entering a valid reason and confirming removal.</returns>
    [Fact]
    public async Task EventMembersPanelRemoveMemberUsesDialogWithSelectedMemberAndWaitsForConfirm()
    {
        var eventId = Guid.Parse("6d000000-0000-0000-0000-0000000000d1");
        var user = new PersonSummary(Guid.Parse("6d000000-0000-0000-0000-0000000000d2"),
            "Selected member", "selected.member@example.invalid");
        var removals = new List<(Guid EventId, Guid UserId, string Reason)>();
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            return method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(
                    Summary(eventId, "Managed Event", EventStatus.Active, true, true), "Owner details")),
                nameof(IEventService.ListMembersAsync) => Task.FromResult(new PageResult<MembershipSummary>(
                    [new(user, MembershipStatus.Active, false)], 1, 1, 25)),
                nameof(IEventService.RemoveMemberAsync) => RecordMemberRemoval(arguments),
                _ => throw new NotSupportedException(method.Name)
            };
        }));
        var cut = Render<EventMembersPanel>(parameters => parameters.Add(component => component.EventId, eventId));

        await ClickButtonAsync(cut, "Remove membership…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
            candidate => candidate.Instance.Open);
        Assert.Equal("Remove Event membership", dialog.Instance.Title);
        Assert.Equal("Remove membership", dialog.Instance.ConfirmLabel);
        Assert.Contains(user.Label, dialog.Markup);
        Assert.Empty(removals);
        const string reason = "Confirmed member departure.";
        await cut.InvokeAsync(() => dialog.FindComponent<FluentTextArea>().Instance.ValueChanged.InvokeAsync(reason));
        Assert.Empty(removals);
        await ClickButtonAsync(dialog, "Remove membership");

        Assert.Equal((eventId, user.Id, reason), Assert.Single(removals));

        Task RecordMemberRemoval(object?[]? arguments)
        {
            removals.Add(((Guid)arguments![0]!, (Guid)arguments[1]!, (string)arguments[2]!));
            return Task.CompletedTask;
        }
    }

    /// <summary>Owner-role removal identifies the selected owner in the shared dialog and dismissal performs no mutation.</summary>
    /// <returns>Completion after dismissing the selected owner's removal dialog.</returns>
    [Fact]
    public async Task EventMembersPanelRemoveOwnerRoleUsesDialogWithSelectedOwnerAndDismissDoesNotMutate()
    {
        var eventId = Guid.Parse("6e000000-0000-0000-0000-0000000000e1");
        var owner = new PersonSummary(Guid.Parse("6e000000-0000-0000-0000-0000000000e2"),
            "Equal owner", "equal.owner@example.invalid");
        var ownerRemovals = new List<(Guid EventId, Guid UserId)>();
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            return method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(
                    Summary(eventId, "Owner Event", EventStatus.Active, true, true), "Owner details")),
                nameof(IEventService.ListMembersAsync) => Task.FromResult(new PageResult<MembershipSummary>(
                    [new(owner, MembershipStatus.Active, true)], 1, 1, 25)),
                nameof(IEventService.RemoveOwnerAsync) => RecordOwnerRemoval(arguments),
                _ => throw new NotSupportedException(method.Name)
            };
        }));
        var cut = Render<EventMembersPanel>(parameters => parameters.Add(component => component.EventId, eventId));

        await ClickButtonAsync(cut, "Remove owner role…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
            candidate => candidate.Instance.Open);
        Assert.Equal("Remove owner role", dialog.Instance.Title);
        Assert.Equal("Remove owner role", dialog.Instance.ConfirmLabel);
        Assert.Contains(owner.Label, dialog.Markup);
        Assert.Empty(ownerRemovals);
        await ClickButtonAsync(dialog, "Keep current state");

        Assert.Empty(ownerRemovals);
        Assert.False(dialog.Instance.Open);

        Task RecordOwnerRemoval(object?[]? arguments)
        {
            ownerRemovals.Add(((Guid)arguments![0]!, (Guid)arguments[1]!));
            return Task.CompletedTask;
        }
    }

    /// <summary>Removal dialogs have no checkbox confirmation, while explicit restoration and equal-owner consent retain their named checkboxes.</summary>
    /// <returns>Completion after selecting a directory user to reveal both legitimate consent controls.</returns>
    [Fact]
    public async Task EventMembersPanelDestructiveActionsHaveNoConfirmationCheckboxButRetainConsentCheckboxes()
    {
        var eventId = Guid.Parse("6f000000-0000-0000-0000-0000000000f1");
        var member = new PersonSummary(Guid.Parse("6f000000-0000-0000-0000-0000000000f2"),
            "Removable member", "removable@example.invalid");
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, _) =>
            method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(
                    Summary(eventId, "Consent Event", EventStatus.Active, true, true), "Owner details")),
                nameof(IEventService.ListMembersAsync) => Task.FromResult(new PageResult<MembershipSummary>(
                    [new(member, MembershipStatus.Active, false)], 1, 1, 25)),
                _ => throw new NotSupportedException(method.Name)
            }));
        var cut = Render<EventMembersPanel>(parameters => parameters.Add(component => component.EventId, eventId));
        var selected = new DirectoryUser(Guid.NewGuid(), Guid.NewGuid(), "Prospective owner",
            "prospective.owner@example.invalid", true);

        await cut.InvokeAsync(() => cut.FindComponent<DirectoryPicker>().Instance.UserSelected.InvokeAsync(selected));
        var consentNames = cut.FindComponents<FluentCheckbox>().Select(checkbox => checkbox.Instance.Name!).ToArray();
        Assert.Equal(["event-member-restore", "event-owner-confirmation"], consentNames);
        await ClickButtonAsync(cut, "Remove membership…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
            candidate => candidate.Instance.Open);

        Assert.Empty(dialog.FindComponents<FluentCheckbox>());
        Assert.Equal(["event-member-restore", "event-owner-confirmation"],
            cut.FindComponents<FluentCheckbox>().Select(checkbox => checkbox.Instance.Name!));
    }

    /// <summary>Invitation revocation binds the selected invitation and recipient to the shared dialog and revokes exactly once after confirmation.</summary>
    /// <returns>Completion after confirming the selected pending invitation.</returns>
    [Fact]
    public async Task EventInvitationsPanelRevokeUsesDialogWithSelectedInvitationAndRecipient()
    {
        var eventId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var invitationId = Guid.Parse("70000000-0000-0000-0000-000000000002");
        var recipient = new PersonSummary(Guid.Parse("70000000-0000-0000-0000-000000000003"),
            "Invited person", "invited.person@example.invalid");
        var revocations = new List<Guid>();
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
            method.Name switch
            {
                nameof(IEventService.ListInvitationsAsync) => Task.FromResult(new PageResult<EventInvitationSummary>(
                    [new(invitationId, eventId, "Invitation Event", recipient, EventInvitationStatus.Pending,
                        new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero))], 1, 1, 25)),
                nameof(IEventService.RevokeInvitationAsync) => RecordRevocation(arguments),
                _ => throw new NotSupportedException(method.Name)
            }));
        var cut = Render<EventInvitationsPanel>(parameters => parameters.Add(component => component.EventId, eventId));

        await ClickButtonAsync(cut, "Revoke invitation…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
            candidate => candidate.Instance.Open);
        Assert.Equal("Revoke pending invitation", dialog.Instance.Title);
        Assert.Equal("Revoke invitation", dialog.Instance.ConfirmLabel);
        Assert.Contains(recipient.Label, dialog.Markup);
        Assert.Empty(revocations);
        await ClickButtonAsync(dialog, "Revoke invitation");

        Assert.Equal(invitationId, Assert.Single(revocations));

        Task RecordRevocation(object?[]? arguments)
        {
            revocations.Add((Guid)arguments![0]!);
            return Task.CompletedTask;
        }
    }

    /// <summary>Dismissing invitation revocation performs no mutation and the destructive dialog contains no checkbox confirmation.</summary>
    /// <returns>Completion after dismissing a pending invitation's rendered dialog.</returns>
    [Fact]
    public async Task EventInvitationsPanelRevokeDismissesWithoutMutationAndHasNoDestructiveCheckbox()
    {
        var eventId = Guid.Parse("71000000-0000-0000-0000-000000000001");
        var invitationId = Guid.Parse("71000000-0000-0000-0000-000000000002");
        var recipient = new PersonSummary(Guid.Parse("71000000-0000-0000-0000-000000000003"),
            "Retained invitee", "retained.invitee@example.invalid");
        var revokeCalls = 0;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, _) =>
            method.Name switch
            {
                nameof(IEventService.ListInvitationsAsync) => Task.FromResult(new PageResult<EventInvitationSummary>(
                    [new(invitationId, eventId, "Retained Event", recipient, EventInvitationStatus.Pending,
                        new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero))], 1, 1, 25)),
                nameof(IEventService.RevokeInvitationAsync) => CountRevocation(),
                _ => throw new NotSupportedException(method.Name)
            }));
        var cut = Render<EventInvitationsPanel>(parameters => parameters.Add(component => component.EventId, eventId));

        await ClickButtonAsync(cut, "Revoke invitation…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
            candidate => candidate.Instance.Open);
        Assert.Empty(dialog.FindComponents<FluentCheckbox>());
        Assert.Empty(cut.FindComponents<FluentCheckbox>());
        await ClickButtonAsync(dialog, "Keep current state");

        Assert.Equal(0, revokeCalls);
        Assert.False(dialog.Instance.Open);

        Task CountRevocation()
        {
            revokeCalls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Create mode uses exact active-creation wording in both the page heading and submit action, never the obsolete Draft action.</summary>
    /// <returns>Completion after the authorized create route loads its editor.</returns>
    [Fact]
    public async Task EventEditPageCreateModeShowsCreateEventAndNeverSaveDraft()
    {
        Services.AddSingleton(TimeProvider.System);
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.ListAsync), method.Name);
            Assert.Equal(EventListKind.Mine, (EventListKind)arguments![0]!);
            return Task.FromResult(new PageResult<EventSummary>([], 0, 1, 1));
        }));

        var cut = Render<EventEditPage>();

        Assert.Equal("Create Event", cut.Find("h1").TextContent.Trim());
        var editor = cut.FindComponent<EventEditor>();
        Assert.Equal("Create Event", editor.Instance.SubmitLabel);
        Assert.Equal("Create Event", Assert.Single(editor.FindComponents<FluentButton>())
            .Find("fluent-button").TextContent.Trim());
        Assert.DoesNotContain("Save Draft", cut.Markup);
    }

    /// <summary>Successful create submits the exact edited model once and navigates to the identifier returned by the Event service.</summary>
    /// <returns>Completion after duplicate review, creation, and navigation finish.</returns>
    [Fact]
    public async Task EventEditPageCreateSuccessNavigatesToCreatedEvent()
    {
        Services.AddSingleton(TimeProvider.System);
        var createdId = Guid.Parse("72000000-0000-0000-0000-000000000001");
        var submitted = new List<EventInput>();
        var duplicateChecks = new List<(string Name, DateOnly Start, DateOnly End)>();
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
            method.Name switch
            {
                nameof(IEventService.ListAsync) => Task.FromResult(new PageResult<EventSummary>([], 0, 1, 1)),
                nameof(IEventService.FindDuplicatesAsync) => RecordDuplicateCheck(arguments),
                nameof(IEventService.CreateAsync) => RecordCreate(arguments),
                _ => throw new NotSupportedException(method.Name)
            }));
        var cut = Render<EventEditPage>();
        var input = Input("Created Event") with
        {
            Description = "Created private description",
            DiscoverySummary = "Created public discovery"
        };

        await cut.InvokeAsync(() => cut.FindComponent<EventEditor>().Instance.Submitted.InvokeAsync(input));

        Assert.Equal(("Created Event", input.StartDate, input.EndDate), Assert.Single(duplicateChecks));
        Assert.Equal(input, Assert.Single(submitted));
        Assert.EndsWith($"/events/{createdId}",
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);

        Task<IReadOnlyList<DuplicateEvent>> RecordDuplicateCheck(object?[]? arguments)
        {
            duplicateChecks.Add(((string)arguments![0]!, (DateOnly)arguments[1]!, (DateOnly)arguments[2]!));
            return Task.FromResult<IReadOnlyList<DuplicateEvent>>([]);
        }

        Task<Guid> RecordCreate(object?[]? arguments)
        {
            submitted.Add((EventInput)arguments![0]!);
            return Task.FromResult(createdId);
        }
    }

    /// <summary>Edit mode retains its exact save wording and does not expose the create action.</summary>
    /// <returns>Completion after the authorized existing Event loads into the editor.</returns>
    [Fact]
    public async Task EventEditPageEditModeUsesSaveChanges()
    {
        Services.AddSingleton(TimeProvider.System);
        var eventId = Guid.Parse("73000000-0000-0000-0000-000000000001");
        var detail = new EventDetail(
            Summary(eventId, "Existing Event", EventStatus.Active, true, true),
            "Existing private description");
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.GetAsync), method.Name);
            Assert.Equal(eventId, (Guid)arguments![0]!);
            return Task.FromResult(detail);
        }));

        var cut = Render<EventEditPage>(parameters => parameters.Add(component => component.Id, eventId));

        Assert.Equal("Edit Event", cut.Find("h1").TextContent.Trim());
        var editor = cut.FindComponent<EventEditor>();
        Assert.Equal("Save changes", editor.Instance.SubmitLabel);
        Assert.Equal("Save changes", Assert.Single(editor.FindComponents<FluentButton>())
            .Find("fluent-button").TextContent.Trim());
        Assert.DoesNotContain("Create Event", cut.Markup);
        Assert.DoesNotContain("Save Draft", cut.Markup);
    }

    private static Task ClickButtonAsync(
        IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> cut,
        string label) =>
        cut.InvokeAsync(() => cut.FindComponents<FluentButton>()
            .Single(button => button.Find("fluent-button").TextContent.Trim() == label)
            .Instance.OnClick.InvokeAsync());

    private async Task ConfigureEventPageAsync(IEventService events)
    {
        Services.AddLogging();
        var experience = new ExperienceCoordinator();
        Services.AddSingleton(experience);
        Services.AddSingleton(new EventCircuitRevalidation());
        Services.AddSingleton(events);
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventManagementQueries>((method, _) =>
            throw new NotSupportedException(method.Name)));
        await experience.ReportConnectionAsync(true, null);
        SetRendererInfo(new("Server", true));
    }

    private static EventSummary Summary(
        Guid id,
        string name,
        EventStatus status,
        bool isMember,
        bool isOwner = false) =>
        new(id, name, $"{name} discovery", new(2026, 9, 10), new(2026, 9, 12),
            "Europe/Prague", status, [], isMember, isOwner, $"{name}-version");

    /// <summary>Confirming owner-role removal dispatches only the owner operation with the selected Event and user identifiers.</summary>
    /// <returns>Completion after the real dialog callback and subsequent panel refresh.</returns>
    [Fact]
    public async Task EventMembersPanelOwnerRemovalConfirmCallsExactOwnerCommandAndNeverMemberCommand()
    {
        var eventId = Guid.Parse("74000000-0000-0000-0000-000000000001");
        var userId = Guid.Parse("74000000-0000-0000-0000-000000000002");
        var owner = new PersonSummary(userId, "Owner to remove", "owner.remove@example.invalid");
        var ownerRemovals = new List<(Guid EventId, Guid UserId)>();
        var memberRemovalCalls = 0;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
            method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(
                    Summary(eventId, "Owner command", EventStatus.Active, true, true), "Owner details")),
                nameof(IEventService.ListMembersAsync) => Task.FromResult(new PageResult<MembershipSummary>(
                    [new(owner, MembershipStatus.Active, true)], 1, 1, 25)),
                nameof(IEventService.RemoveOwnerAsync) => RecordOwnerRemoval(arguments),
                nameof(IEventService.RemoveMemberAsync) => CountMemberRemoval(),
                _ => throw new NotSupportedException(method.Name)
            }));
        var cut = Render<EventMembersPanel>(parameters => parameters.Add(component => component.EventId, eventId));

        await ClickButtonAsync(cut, "Remove owner role…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(), candidate => candidate.Instance.Open);
        await ClickButtonAsync(dialog, "Remove owner role");

        Assert.Equal((eventId, userId), Assert.Single(ownerRemovals));
        Assert.Equal(0, memberRemovalCalls);
        Assert.False(dialog.Instance.Open);

        Task RecordOwnerRemoval(object?[]? arguments)
        {
            ownerRemovals.Add(((Guid)arguments![0]!, (Guid)arguments[1]!));
            return Task.CompletedTask;
        }

        Task CountMemberRemoval()
        {
            memberRemovalCalls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Required-input gating disables confirmation independently of host busy state without disabling dismissal.</summary>
    /// <returns>Completion after disabled confirmation is suppressed and enabled dismissal is delivered.</returns>
    [Fact]
    public async Task ConfirmActionDialogConfirmDisabledWithoutBusySuppressesOnlyConfirmation()
    {
        var confirmed = 0;
        var dismissed = 0;
        var cut = Render<ConfirmActionDialog>(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Title, "Reason required")
            .Add(component => component.ConfirmLabel, "Confirm action")
            .Add(component => component.Busy, false)
            .Add(component => component.ConfirmDisabled, true)
            .Add(component => component.Confirmed, () => confirmed++)
            .Add(component => component.Dismissed, () => dismissed++));
        var confirm = cut.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Confirm action");
        var dismiss = cut.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Keep current state");

        Assert.True(confirm.Instance.Disabled);
        Assert.False(dismiss.Instance.Disabled);
        confirm.Find("fluent-button").Click();
        Assert.Equal(0, confirmed);
        Assert.Equal(0, dismissed);
        await cut.InvokeAsync(() => dismiss.Instance.OnClick.InvokeAsync());

        Assert.Equal(0, confirmed);
        Assert.Equal(1, dismissed);
    }

    /// <summary>The cancellation host enables its shared confirmation at exactly ten trimmed reason characters.</summary>
    /// <param name="reason">Raw reason text at the accepted boundary, immediately below it, or padded below it.</param>
    /// <param name="disabled">Expected confirmation gate state.</param>
    /// <returns>Completion after impact loading and reason binding update the real lifecycle dialog.</returns>
    [Theory]
    [InlineData("RRRRRRRRR", true)]
    [InlineData("RRRRRRRRRR", false)]
    [InlineData(" RRRRRRRRR ", true)]
    public async Task EventLifecycleCancellationReasonBoundaryControlsConfirmation(string reason, bool disabled)
    {
        Services.AddLogging();
        var experience = new ExperienceCoordinator();
        Services.AddSingleton(experience);
        Services.AddSingleton(new EventCircuitRevalidation());
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventManagementQueries>((method, _) =>
        {
            Assert.Equal(nameof(IEventManagementQueries.GetCancellationImpactAsync), method.Name);
            return Task.FromResult(2);
        }));
        var mutationCalls = 0;
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, _) =>
        {
            Assert.Equal(nameof(IEventService.ChangeStatusAsync), method.Name);
            mutationCalls++;
            return Task.CompletedTask;
        }));
        await experience.ReportConnectionAsync(true, null);
        SetRendererInfo(new("Server", true));
        var item = Summary(Guid.Parse("75000000-0000-0000-0000-000000000001"),
            "Boundary cancellation", EventStatus.Active, true, true);
        var cut = Render<EventLifecycle>(parameters => parameters.Add(component => component.Item, item));

        await ClickButtonAsync(cut, "Cancel Event…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(), candidate => candidate.Instance.Open);
        await cut.InvokeAsync(() => dialog.FindComponent<FluentTextArea>().Instance.ValueChanged
            .InvokeAsync(reason));

        Assert.Equal(disabled, dialog.Instance.ConfirmDisabled);
        var confirm = dialog.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Cancel Event and affected Quests");
        Assert.Equal(disabled, confirm.Instance.Disabled);
        Assert.Equal(0, mutationCalls);
    }

    /// <summary>The membership-removal host enables its shared confirmation at exactly ten trimmed reason characters.</summary>
    /// <param name="reason">Raw reason text at the accepted boundary, immediately below it, or padded below it.</param>
    /// <param name="disabled">Expected confirmation gate state.</param>
    /// <returns>Completion after selecting a member and binding the real removal reason input.</returns>
    [Theory]
    [InlineData("RRRRRRRRR", true)]
    [InlineData("RRRRRRRRRR", false)]
    [InlineData(" RRRRRRRRR ", true)]
    public async Task EventMembersPanelRemovalReasonBoundaryControlsConfirmation(string reason, bool disabled)
    {
        var eventId = Guid.Parse("76000000-0000-0000-0000-000000000001");
        var member = new PersonSummary(Guid.Parse("76000000-0000-0000-0000-000000000002"),
            "Boundary member", "boundary.member@example.invalid");
        var removalCalls = 0;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, _) =>
            method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(
                    Summary(eventId, "Boundary removal", EventStatus.Active, true, true), "Owner details")),
                nameof(IEventService.ListMembersAsync) => Task.FromResult(new PageResult<MembershipSummary>(
                    [new(member, MembershipStatus.Active, false)], 1, 1, 25)),
                nameof(IEventService.RemoveMemberAsync) => CountRemoval(),
                _ => throw new NotSupportedException(method.Name)
            }));
        var cut = Render<EventMembersPanel>(parameters => parameters.Add(component => component.EventId, eventId));

        await ClickButtonAsync(cut, "Remove membership…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(), candidate => candidate.Instance.Open);
        await cut.InvokeAsync(() => dialog.FindComponent<FluentTextArea>().Instance.ValueChanged
            .InvokeAsync(reason));

        Assert.Equal(disabled, dialog.Instance.ConfirmDisabled);
        var confirm = dialog.FindComponents<FluentButton>().Single(button =>
            button.Find("fluent-button").TextContent.Trim() == "Remove membership");
        Assert.Equal(disabled, confirm.Instance.Disabled);
        Assert.Equal(0, removalCalls);

        Task CountRemoval()
        {
            removalCalls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>A successful leave command navigates to the canonical Events collection route, not a detail or relative variant.</summary>
    /// <returns>Completion after confirming through the rendered leave dialog.</returns>
    [Fact]
    public async Task EventDetailPageSuccessfulLeaveNavigatesExactlyToEvents()
    {
        var eventId = Guid.Parse("77000000-0000-0000-0000-000000000001");
        var leaveCalls = 0;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            if (method.Name == nameof(IEventService.LeaveAsync))
            {
                Assert.Equal(eventId, (Guid)arguments![0]!);
                leaveCalls++;
                return Task.CompletedTask;
            }

            Assert.Equal(nameof(IEventService.GetAsync), method.Name);
            return Task.FromResult(new EventDetail(
                Summary(eventId, "Navigate after leave", EventStatus.Active, true), "Member details"));
        }));
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = Render<EventDetailPage>(parameters => parameters.Add(component => component.Id, eventId));

        await ClickButtonAsync(cut, "Leave Event…");
        await ClickButtonAsync(
            Assert.Single(cut.FindComponents<ConfirmActionDialog>(), candidate => candidate.Instance.Open),
            "Leave Event");

        Assert.Equal(1, leaveCalls);
        Assert.Equal(new Uri(new Uri(navigation.BaseUri), "/events").AbsoluteUri, navigation.Uri);
    }

    /// <summary>A successful legacy-Draft deletion navigates to the canonical Events collection route.</summary>
    /// <returns>Completion after confirming through the rendered deletion dialog.</returns>
    [Fact]
    public async Task EventLifecycleSuccessfulLegacyDraftDeleteNavigatesExactlyToEvents()
    {
        Services.AddLogging();
        var experience = new ExperienceCoordinator();
        Services.AddSingleton(experience);
        Services.AddSingleton(new EventCircuitRevalidation());
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventManagementQueries>((method, _) =>
            throw new NotSupportedException(method.Name)));
        var item = Summary(Guid.Parse("78000000-0000-0000-0000-000000000001"),
            "Navigate after delete", EventStatus.Draft, true, true);
        var deleteCalls = 0;
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.DeleteDraftAsync), method.Name);
            Assert.Equal(item.Id, (Guid)arguments![0]!);
            Assert.Equal(item.Version, (string)arguments[1]!);
            deleteCalls++;
            return Task.CompletedTask;
        }));
        await experience.ReportConnectionAsync(true, null);
        SetRendererInfo(new("Server", true));
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = Render<EventLifecycle>(parameters => parameters.Add(component => component.Item, item));

        await ClickButtonAsync(cut, "Delete legacy Draft…");
        await ClickButtonAsync(
            Assert.Single(cut.FindComponents<ConfirmActionDialog>(),
                candidate => candidate.Instance.Open && candidate.Instance.Title == "Delete legacy Draft Event"),
            "Delete Draft permanently");

        Assert.Equal(1, deleteCalls);
        Assert.Equal(new Uri(new Uri(navigation.BaseUri), "/events").AbsoluteUri, navigation.Uri);
    }

    /// <summary>Successful member removal reloads the panel from the stateful service snapshot so the removed row disappears.</summary>
    /// <returns>Completion after mutation, refresh, and empty-state rendering.</returns>
    [Fact]
    public async Task EventMembersPanelRemovalRefreshesAndRemovesMutatedRow()
    {
        var eventId = Guid.Parse("79000000-0000-0000-0000-000000000001");
        var member = new PersonSummary(Guid.Parse("79000000-0000-0000-0000-000000000002"),
            "Removed row", "removed.row@example.invalid");
        var present = true;
        var listReads = 0;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
            method.Name switch
            {
                nameof(IEventService.GetAsync) => Task.FromResult(new EventDetail(
                    Summary(eventId, "Stateful members", EventStatus.Active, true, true), "Owner details")),
                nameof(IEventService.ListMembersAsync) => ListMembers(),
                nameof(IEventService.RemoveMemberAsync) => RemoveMember(arguments),
                _ => throw new NotSupportedException(method.Name)
            }));
        var cut = Render<EventMembersPanel>(parameters => parameters.Add(component => component.EventId, eventId));
        Assert.Contains(member.Label, cut.Markup);

        await ClickButtonAsync(cut, "Remove membership…");
        var dialog = Assert.Single(cut.FindComponents<ConfirmActionDialog>(), candidate => candidate.Instance.Open);
        await cut.InvokeAsync(() => dialog.FindComponent<FluentTextArea>().Instance.ValueChanged
            .InvokeAsync("Exactly valid removal reason"));
        await ClickButtonAsync(dialog, "Remove membership");

        Assert.False(present);
        Assert.Equal(2, listReads);
        Assert.DoesNotContain(member.Label, cut.Find("ul.management-list").TextContent);
        Assert.Contains("No individual membership records in this view.", cut.Markup);

        Task<PageResult<MembershipSummary>> ListMembers()
        {
            listReads++;
            IReadOnlyList<MembershipSummary> items = present
                ? [new(member, MembershipStatus.Active, false)]
                : [];
            return Task.FromResult(new PageResult<MembershipSummary>(items, items.Count, 1, 25));
        }

        Task RemoveMember(object?[]? arguments)
        {
            Assert.Equal(eventId, (Guid)arguments![0]!);
            Assert.Equal(member.Id, (Guid)arguments[1]!);
            present = false;
            return Task.CompletedTask;
        }
    }

    /// <summary>Successful invitation revocation reloads the panel from the stateful service snapshot so the revoked row disappears.</summary>
    /// <returns>Completion after mutation, refresh, and empty-state rendering.</returns>
    [Fact]
    public async Task EventInvitationsPanelRevocationRefreshesAndRemovesMutatedRow()
    {
        var eventId = Guid.Parse("7a000000-0000-0000-0000-000000000001");
        var invitationId = Guid.Parse("7a000000-0000-0000-0000-000000000002");
        var recipient = new PersonSummary(Guid.Parse("7a000000-0000-0000-0000-000000000003"),
            "Revoked row", "revoked.row@example.invalid");
        var present = true;
        var listReads = 0;
        await ConfigureEventPageAsync(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
            method.Name switch
            {
                nameof(IEventService.ListInvitationsAsync) => ListInvitations(),
                nameof(IEventService.RevokeInvitationAsync) => Revoke(arguments),
                _ => throw new NotSupportedException(method.Name)
            }));
        var cut = Render<EventInvitationsPanel>(parameters => parameters.Add(component => component.EventId, eventId));
        Assert.Contains(recipient.Label, cut.Markup);

        await ClickButtonAsync(cut, "Revoke invitation…");
        await ClickButtonAsync(
            Assert.Single(cut.FindComponents<ConfirmActionDialog>(), candidate => candidate.Instance.Open),
            "Revoke invitation");

        Assert.False(present);
        Assert.Equal(2, listReads);
        Assert.DoesNotContain(recipient.Label, cut.Find("div.management-list").TextContent);
        Assert.Contains("No Event invitations.", cut.Markup);

        Task<PageResult<EventInvitationSummary>> ListInvitations()
        {
            listReads++;
            IReadOnlyList<EventInvitationSummary> items = present
                ? [new(invitationId, eventId, "Stateful invitations", recipient, EventInvitationStatus.Pending,
                    new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero))]
                : [];
            return Task.FromResult(new PageResult<EventInvitationSummary>(items, items.Count, 1, 25));
        }

        Task Revoke(object?[]? arguments)
        {
            Assert.Equal(invitationId, (Guid)arguments![0]!);
            present = false;
            return Task.CompletedTask;
        }
    }

    /// <summary>A busy join card renders its direct action disabled and a rendered click cannot dispatch the card identifier.</summary>
    [Fact]
    public void EventCardBusyJoinIsDisabledAndCallbackFree()
    {
        var eventId = Guid.Parse("7b000000-0000-0000-0000-000000000001");
        var callbacks = new List<Guid>();
        var cut = Render<EventCard>(parameters => parameters
            .Add(component => component.Item,
                Summary(eventId, "Busy join", EventStatus.Active, false))
            .Add(component => component.ShowJoinAction, true)
            .Add(component => component.Busy, true)
            .Add(component => component.JoinRequested, value => callbacks.Add(value)));
        var join = Assert.Single(cut.FindComponents<FluentButton>());

        Assert.True(join.Instance.Disabled);
        join.Find("fluent-button").Click();

        Assert.Empty(callbacks);
        Assert.Contains("Join Event", cut.Markup);
    }

    /// <summary>Disabled management tabs retain their complete owner surface while rendered clicks emit no selection callback.</summary>
    [Fact]
    public void EventManagementTabsDisabledAreCallbackFree()
    {
        var selections = new List<string>();
        var cut = Render<EventManagementTabs>(parameters => parameters
            .Add(component => component.IsOwner, true)
            .Add(component => component.Selected, "overview")
            .Add(component => component.Disabled, true)
            .Add(component => component.SelectedChanged, value => selections.Add(value)));
        var tabs = cut.FindAll("[role=tab]");

        Assert.Equal(5, tabs.Count);
        Assert.All(tabs, tab => Assert.True(tab.HasAttribute("disabled")));
        ((AngleSharp.Html.Dom.IHtmlElement)tabs.Single(tab => tab.TextContent.Trim() == "Settings")).DoClick();

        Assert.Empty(selections);
        Assert.Equal("overview", cut.Instance.Selected);
        Assert.True(tabs.Single(tab => tab.TextContent.Trim() == "Overview")
            .HasAttribute("aria-selected"));
    }
}
