using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Web.Components.Quests;

namespace Sidequest.UnitTests.CoreQuests;

/// <summary>Exercises the retained Quest-management surface after moderation removal.</summary>
public sealed class QuestManagementLifecycleTests : BunitContext
{
    /// <summary>Registers the package-owned Fluent components used by management controls.</summary>
    public QuestManagementLifecycleTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>A legacy Suspended Quest remains editable and cancellable by a manager without exposing reinstate or suspend actions.</summary>
    [Fact]
    public void LegacySuspendedManager_RetainsEditAndCancelWithoutModerationTransitions()
    {
        var cut = Render<QuestManagement>(parameters => parameters.Add(component => component.Detail,
            Detail(QuestStatus.Suspended)));

        Assert.Equal($"/quests/{cut.Instance.Detail.Summary.Id}/edit",
            cut.FindAll("a").Single(link => link.TextContent.Contains("Edit", StringComparison.Ordinal)).GetAttribute("href"));
        Assert.Contains("Cancel Quest", cut.Markup);
        Assert.DoesNotContain("Suspend", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reinstate", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Legacy Suspended managers retain attendee removal with the selected account and concrete reason.</summary>
    /// <returns>Completion after the actual confirmation callback is dispatched.</returns>
    [Fact]
    public async Task LegacySuspendedManager_CanRequestAttendeeRemoval()
    {
        var attendee = new QuestRosterPersonSummary(Guid.NewGuid(), "Legacy attendee");
        var requests = new List<QuestActionRequest>();
        var cut = Render<QuestManagement>(parameters => parameters
            .Add(component => component.Detail, Detail(QuestStatus.Suspended) with { Attendees = [attendee] })
            .Add(component => component.Execute, request => requests.Add(request)));

        cut.FindAll("button").Single(button => button.TextContent == "Remove attendee").Click();
        cut.Find("select").Change(attendee.Id.ToString());
        cut.Find("fluent-text-area").Input("Legacy suspended attendance cleanup.");
        await cut.InvokeAsync(() => cut.FindComponents<FluentButton>()
            .Single(button => button.Markup.Contains("Confirm: Remove attendee", StringComparison.Ordinal))
            .Instance.OnClick.InvokeAsync());

        Assert.Equal(new QuestActionRequest("remove-attendee", attendee.Id, "Legacy suspended attendance cleanup."),
            Assert.Single(requests));
    }

    /// <summary>Active managers receive ordinary owner controls but no removed suspend or reinstate action.</summary>
    [Fact]
    public void ActiveManager_HasNoSuspendOrReinstateAction()
    {
        var cut = Render<QuestManagement>(parameters => parameters.Add(component => component.Detail,
            Detail(QuestStatus.Active)));

        Assert.Contains("Cancel Quest", cut.Markup);
        Assert.DoesNotContain("Suspend", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reinstate", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    private static QuestDetail Detail(QuestStatus status)
    {
        var summary = new QuestSummary(Guid.NewGuid(), Guid.NewGuid(), "Parent Event", "Quest title", "Room",
            new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
            "Europe/Prague", status, QuestVisibility.Private, 1, 0, null, ParticipationStatus.None,
            true, "version", null)
        {
            CanManage = true
        };
        return new(summary, "Authorized description", "", [], [], [], []);
    }
}
