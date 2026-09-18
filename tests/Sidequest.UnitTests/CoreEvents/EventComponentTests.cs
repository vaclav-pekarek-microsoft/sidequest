using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Events.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.UnitTests.SecondaryExperience;
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
        var reload = Assert.Single(Reloads());
        Assert.False(reload.Instance.Disabled);
        fail = false;
        var beforeRetry = reads;
        await cut.InvokeAsync(() => reload.Instance.OnClick.InvokeAsync());
        Assert.Equal(beforeRetry + (pageType == typeof(EventMembersPage) ? 2 : 1), reads);
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
        var cut = Render<EventCard>(p => p.Add(x => x.Item, item));
        Assert.Equal($"/events/{id}", cut.Find("h2 a").GetAttribute("href"));
        Assert.Equal("<script>sentinel</script>", cut.Find("h2").TextContent);
        Assert.Contains("Safe public discovery", cut.Markup);
        Assert.Contains("owner@example.invalid", cut.Markup);
        Assert.Contains("owner@example.invalid (Equal owner)", cut.Markup);
        Assert.DoesNotContain(ownerId.ToString(), cut.Markup);
        Assert.Contains("Membership is required for full content.", cut.Markup);
        Assert.Empty(cut.FindAll("script"));
        Assert.DoesNotContain("opaque-version", cut.Markup);
        Assert.DoesNotContain("You are a member", cut.Markup);
        Assert.Empty(cut.FindAll("button, textarea, table"));
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

    /// <summary>Reveals cancellation only on request, retains its reason when hidden, and still requires impact review and explicit confirmation.</summary>
    /// <returns>Completion after checking the untouched lifecycle command's Event, version, target, and preserved reason.</returns>
    [Fact]
    public async Task CancellationIsCollapsedAndRetainsReviewedReason()
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

        Assert.Empty(cut.FindComponents<FluentTextArea>());
        Assert.Empty(cut.FindComponents<FluentCheckbox>());
        Assert.Equal(0, impactReads);
        await ClickAsync("Cancel Event…");
        Assert.False(cut.Find("fieldset").HasAttribute("disabled"));
        var cancel = cut.FindComponents<FluentButton>().Single(x =>
            x.Find("fluent-button").TextContent.Trim() == "Cancel Event and affected Quests");
        Assert.True(cancel.Instance.Disabled);
        const string reason = "Retain this cancellation explanation.";
        await cut.InvokeAsync(() => cut.FindComponent<FluentTextArea>().Instance.ValueChanged.InvokeAsync(reason));
        await ClickAsync("Hide cancellation");
        Assert.Empty(cut.FindComponents<FluentTextArea>());
        await ClickAsync("Cancel Event…");
        Assert.Equal(reason, cut.FindComponent<FluentTextArea>().Instance.Value);
        Assert.Empty(commands);
        await ClickAsync("Review cancellation impact");
        Assert.Equal(1, impactReads);
        Assert.Contains("3 Draft, Active, or Suspended child Quests", cut.Markup);
        Assert.True(cut.FindComponents<FluentButton>().Single(x =>
            x.Find("fluent-button").TextContent.Trim() == "Cancel Event and affected Quests").Instance.Disabled);
        await cut.InvokeAsync(() => cut.FindComponent<FluentCheckbox>().Instance.ValueChanged.InvokeAsync(true));
        await ClickAsync("Cancel Event and affected Quests");
        Assert.Equal((item.Id, item.Version, EventStatus.Cancelled, reason), Assert.Single(commands));
        Assert.Empty(cut.FindComponents<FluentTextArea>());
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
}
