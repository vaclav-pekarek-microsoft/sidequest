using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Web.Components.Quests;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.CoreQuests;

/// <summary>Exercises real Fluent Quest controls, explicit participation choices, privacy redaction and validation in bUnit.</summary>
public sealed class QuestComponentTests : BunitContext
{
    /// <summary>Registers package-owned Fluent services; permissive JS emulation is restricted to the unit renderer.</summary>
    public QuestComponentTests()
    {
        Services.AddFluentUIComponents();
        Services.AddSingleton<ExperienceCoordinator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Busy transitions explicitly control Fluent fields without losing the unsaved model or unlocking published visibility.</summary>
    /// <returns>A task completing after disable/enable transitions and immutable parent input assertions.</returns>
    [Fact]
    public async Task EditorBusyTransitions_ExplicitlyUpdateFluentControlsWithoutReplacingInput()
    {
        var input = new QuestInput("Original Quest", "Description", "Room", null, new(2026, 7, 15, 10, 0, 0),
            new(2026, 7, 15, 12, 0, 0), null, null, QuestVisibility.Private);
        var cut = Render<QuestEditor>(p => p.Add(x => x.Initial, input).Add(x => x.ZoneId, "Europe/Prague").Add(x => x.Published, true));
        await cut.InvokeAsync(() => cut.FindComponents<FluentTextField>().First().Instance.ValueChanged.InvokeAsync("Unsaved Quest"));
        cut.Render(p => p.Add(x => x.Busy, true));
        Assert.All(cut.FindComponents<FluentTextField>(), x => Assert.True(x.Instance.Disabled));
        Assert.All(cut.FindComponents<FluentTextArea>(), x => Assert.True(x.Instance.Disabled));
        cut.Render(p => p.Add(x => x.Busy, false));
        Assert.All(cut.FindComponents<FluentTextField>(), x => Assert.False(x.Instance.Disabled));
        Assert.All(cut.FindComponents<FluentTextArea>(), x => Assert.False(x.Instance.Disabled));
        Assert.Equal("Unsaved Quest", cut.FindComponents<FluentTextField>().First().Instance.Value);
        Assert.True(cut.Find("select").HasAttribute("disabled"));
        Assert.Equal("Original Quest", input.Title);
    }

    /// <summary>Joined users get Leave only; none/following users see the exact non-destructive options permitted by status.</summary>
    /// <param name="participation">Current exclusive participation.</param>
    /// <param name="status">Effective lifecycle status supplied by the service.</param>
    /// <param name="labels">Expected button labels in display order.</param>
    [Theory]
    [InlineData(ParticipationStatus.Joined, QuestStatus.Active, "Leave Quest")]
    [InlineData(ParticipationStatus.Joined, QuestStatus.Archived, "Leave Quest")]
    [InlineData(ParticipationStatus.Following, QuestStatus.Active, "Join Quest|Unfollow")]
    [InlineData(ParticipationStatus.Following, QuestStatus.Suspended, "Unfollow")]
    [InlineData(ParticipationStatus.None, QuestStatus.Active, "Join Quest|Follow")]
    [InlineData(ParticipationStatus.None, QuestStatus.Cancelled, "")]
    public void ParticipationControls_PreserveExclusiveActions(ParticipationStatus participation, QuestStatus status, string labels)
    {
        var component = Render<QuestParticipationControls>(p => p.Add(c => c.Summary, Summary() with { Participation = participation, Status = status }));
        Assert.Equal(labels.Length == 0 ? [] : labels.Split('|'),
            component.FindComponents<FluentButton>().Select(b => b.Markup.Contains("Leave Quest") ? "Leave Quest" :
                b.Markup.Contains("Join Quest") ? "Join Quest" : b.Markup.Contains("Unfollow") ? "Unfollow" : "Follow").ToArray());
        Assert.Contains("Leaving never restores Following", component.Markup);
    }

    /// <summary>A joined-user Leave click emits exactly the Leave command, never a hidden follow transition.</summary>
    /// <returns>Completion after dispatching the real Fluent callback.</returns>
    [Fact]
    public async Task LeaveClick_EmitsOnlyLeaveCommand()
    {
        var requests = new List<ParticipationCommand>();
        var component = Render<QuestParticipationControls>(p => p
            .Add(c => c.Summary, Summary() with { Participation = ParticipationStatus.Joined })
            .Add(c => c.Change, command => requests.Add(command)));
        await component.InvokeAsync(() => component.FindComponent<FluentButton>().Instance.OnClick.InvokeAsync());
        Assert.Equal(new[] { ParticipationCommand.Leave }, requests);
    }

    /// <summary>Moderation cards render neither counts nor the ordinary private link; capacity remains advisory for ordinary viewers.</summary>
    [Fact]
    public void Card_RedactsCountsAndUsesDedicatedModerationLink()
    {
        var summary = Summary() with { AttendeeCount = null, FollowerCount = null, Visibility = QuestVisibility.Private };
        var component = Render<QuestCard>(p => p.Add(c => c.Item, summary).Add(c => c.Moderation, true));
        Assert.Equal($"/quests/{summary.Id}?moderation=true", component.Find("a").GetAttribute("href"));
        Assert.DoesNotContain("joined", component.Markup);
        Assert.DoesNotContain("following", component.Markup);
        Assert.Contains("Europe/Prague", component.Markup);
        Assert.Contains("2026-07-15 12:00", component.Markup);
        var ordinary = Render<QuestCard>(p => p.Add(c => c.Item, Summary() with { AttendeeCount = 3, SuggestedCapacity = 1 }));
        Assert.Contains("Above suggested capacity", ordinary.Markup);
        Assert.Contains("Joining is still allowed", ordinary.Markup);
    }

    /// <summary>Moderation controls expose no owner editing, invitation, roster selector, or attendance-management action.</summary>
    [Fact]
    public void Management_ModerationOffersOnlyReasonedSuspend()
    {
        var detail = new QuestDetail(Summary(), "Description", "", [], null, null, null);
        var component = Render<QuestManagement>(p => p.Add(c => c.Detail, detail).Add(c => c.Moderation, true));
        Assert.Empty(component.FindAll("select"));
        Assert.Empty(component.FindAll("a"));
        Assert.Single(component.FindComponents<FluentButton>());
        Assert.Contains("Suspend", component.FindComponent<FluentButton>().Markup);
        Assert.True(component.FindComponent<FluentButton>().Instance.Disabled);
        Assert.Contains("no participant counts or rosters", component.Markup);
    }

    /// <summary>The draft editor validates a short title, never mutates parent input, and disables visibility changes when published.</summary>
    /// <returns>Completion after real Fluent binding and form submission.</returns>
    [Fact]
    public async Task Editor_ValidatesAndCopiesInput_WithoutMutatingParentSnapshot()
    {
        var initial = new QuestInput("Original", "", "", null, new DateTime(2026, 7, 15, 12, 0, 0),
            new DateTime(2026, 7, 15, 14, 0, 0), null, null, QuestVisibility.Private);
        var submissions = new List<QuestEditorModel>();
        var component = Render<QuestEditor>(p => p.Add(c => c.Initial, initial).Add(c => c.ZoneId, "Europe/Prague")
            .Add(c => c.Published, true).Add(c => c.Save, value => submissions.Add(value)));
        var title = component.FindComponents<FluentTextField>().First();
        await component.InvokeAsync(() => title.Instance.ValueChanged.InvokeAsync("ab"));
        component.Find("form").Submit();
        Assert.Empty(submissions);
        Assert.Contains("minimum length", component.Find(".validation-errors").TextContent);
        await component.InvokeAsync(() => title.Instance.ValueChanged.InvokeAsync("Corrected title"));
        component.Find("form").Submit();
        Assert.Equal("Corrected title", Assert.Single(submissions).Title);
        Assert.Equal("Original", initial.Title);
        Assert.NotNull(component.Find("select").GetAttribute("disabled"));
        Assert.Contains("audited moderation view", component.Markup);
    }

    private static QuestSummary Summary() => new(Guid.NewGuid(), Guid.NewGuid(), "Synthetic Event", "Synthetic Quest", "Room",
        new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
        "Europe/Prague", QuestStatus.Active, QuestVisibility.Public, 0, 0, null, ParticipationStatus.None, false, false, "", null);
}
