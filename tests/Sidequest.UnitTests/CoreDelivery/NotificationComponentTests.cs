using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Notifications;
using Sidequest.UnitTests.SecondaryExperience;
using Sidequest.Web.Components.Pages.Notifications;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.CoreDelivery;

/// <summary>Exercises real Fluent controls and service-only notification pages without live JavaScript or external transport.</summary>
public sealed class NotificationComponentTests : BunitContext
{
    private readonly NotificationUiService service = new();
    private readonly ExperienceCoordinator experience = new();

    /// <summary>Registers the existing Fluent stack and a deterministic application boundary for rendering tests.</summary>
    public NotificationComponentTests()
    {
        Services.AddFluentUIComponents();
        Services.AddSingleton<INotificationService>(service);
        Services.AddSingleton(experience);
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((_, _) => throw new NotSupportedException()));
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
    }

    /// <summary>Loading transitions to an explicit empty inbox without displaying raw exception details after an authorization failure.</summary>
    /// <returns>Completion after the held load and a subsequent denied refresh have updated the rendered inbox.</returns>
    [Fact]
    public async Task InboxLoadingEmptyAndDeniedStatesAreSafe()
    {
        await experience.ReportConnectionAsync(true, null);
        service.Inbox = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var component = Render<NotificationInbox>();
        Assert.Contains("Loading notifications", component.Markup);
        await component.InvokeAsync(async () =>
        {
            service.Inbox.SetResult(new PageResult<NotificationSummary>([], 0, 1, 25));
            // Register the check on the renderer, then yield it to the pending query and lifecycle render.
            await component.WaitForAssertionAsync(() =>
            {
                Assert.Contains("No notifications on this page", component.Markup);
                Assert.DoesNotContain("Loading notifications", component.Markup);
                Assert.Equal(1, service.ListCalls);
                Assert.Equal(1, service.CountCalls);
            });
        });
        service.Denied = true;
        var refresh = component.FindComponents<FluentButton>().Single(x => x.Markup.Contains("Refresh"));
        await component.InvokeAsync(() => refresh.Instance.OnClick.InvokeAsync());
        Assert.Contains("no longer has access", component.Find("[role=alert]").TextContent);
        Assert.DoesNotContain("private-secret", component.Markup);
        Assert.DoesNotContain("No notifications on this page", component.Markup);
    }

    /// <summary>Arbitrary decimal hours are accepted while precision errors remain visible and mandatory communication cannot be opted out.</summary>
    /// <returns>Completion after invalid precision is rejected and the corrected preference value is persisted.</returns>
    [Fact]
    public async Task PreferencesValidateExactHoursAndExplainMandatoryMessages()
    {
        await experience.ReportConnectionAsync(true, null);
        var component = Render<NotificationPreferences>();
        Assert.Contains("calendar updates cannot be disabled", component.Markup);
        Assert.Contains("Declining in Outlook does not change Sidequest attendance", component.Markup);
        Assert.Equal(new[] { "Optional email", "Reminders and time zone" },
            component.FindAll("fieldset.section-box > legend.section-heading").Select(x => x.TextContent));
        component.Find("#reminder-hours").Change("0.011");
        component.Find("form").Submit();
        component.WaitForAssertion(() => Assert.Contains("two decimal", component.Find("[role=alert]").TextContent));
        Assert.Null(service.Saved);
        component.Find("#reminder-hours").Change("0.5");
        component.Find("form").Submit();
        component.WaitForAssertion(() => Assert.Equal(.5m, service.Saved?.ReminderHours));
        Assert.Contains("Preferences saved", component.Find("[role=status]").TextContent);
    }

    /// <summary>Replay requires an explicit second action and passes only the redacted record identity/category to the service.</summary>
    /// <returns>Completion after confirmation queues exactly one replay and reports its non-delivery guarantee.</returns>
    [Fact]
    public async Task FailureReplayRequiresConfirmation()
    {
        await experience.ReportConnectionAsync(true, null);
        var id = Guid.NewGuid();
        service.Failures = [new(id, "delivery", "Configuration requires correction.", 1, DateTimeOffset.UnixEpoch)];
        var component = Render<NotificationFailures>();
        var review = component.FindComponents<FluentButton>().Single(x => x.Markup.Contains("Review replay"));
        await component.InvokeAsync(() => review.Instance.OnClick.InvokeAsync());
        Assert.Empty(service.Replays);
        Assert.Contains("Confirm replay", component.Markup);
        Assert.Equal("Failed work", component.Find(".section-box #failed-work-heading").TextContent);
        Assert.Equal("Confirm replay", component.Find(".section-box.danger-zone #replay-heading").TextContent);
        var confirm = component.FindComponents<FluentButton>().Single(x => x.Markup.Contains("Confirm replay"));
        await component.InvokeAsync(() => confirm.Instance.OnClick.InvokeAsync());
        Assert.Equal((id, "delivery"), Assert.Single(service.Replays));
        Assert.Contains("queued, not delivered", component.Find("[role=status]").TextContent);
    }
}
