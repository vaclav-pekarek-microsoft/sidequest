using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Web.Components.Experience;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Checks reusable Quest home statistics, lists, invitation actions, and local date rendering.</summary>
public sealed class ExperienceComponentTests : BunitContext
{
    /// <summary>Registers the real per-circuit experience state for each isolated renderer.</summary>
    public ExperienceComponentTests()
    {
        Services.AddFluentUIComponents();
        Services.AddScoped<ExperienceCoordinator>();
    }

    /// <summary>Prerendered detailed Quest time keeps Event time primary when a device zone later becomes available.</summary>
    /// <returns>Completion after primary ordering and exact civil time assertions.</returns>
    [Fact]
    public async Task EventTimeRemainsPrimaryAndDeviceZoneIsSecondary()
    {
        var component = Render<QuestTimeDisplay>(p => p.Add(x => x.ZoneId, "Europe/Prague")
            .Add(x => x.StartUtc, new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero))
            .Add(x => x.EndUtc, new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Contains("Device time zone unavailable", component.Markup);
        Assert.Contains("12:00", component.Find("time").TextContent);
        await component.InvokeAsync(() => Services.GetRequiredService<ExperienceCoordinator>()
            .ReportConnectionAsync(true, "America/New_York"));
        Assert.Contains("Event time (Europe/Prague)", component.Find("p").TextContent);
        Assert.Contains("06:00", component.FindAll("p")[1].TextContent);
        Assert.DoesNotContain("unavailable", component.Markup);
    }

    /// <summary>The compact date component renders the Quest's Event-local calendar date and machine-readable UTC instant.</summary>
    [Fact]
    public void QuestLocalDateUsesInheritedEventZone()
    {
        var start = new DateTimeOffset(2026, 7, 15, 22, 30, 0, TimeSpan.Zero);
        var component = Render<QuestLocalDate>(parameters => parameters
            .Add(item => item.StartUtc, start)
            .Add(item => item.ZoneId, "Europe/Prague"));
        Assert.Equal("2026-07-16", component.Find("time").TextContent);
        Assert.Equal(start.ToString("O"), component.Find("time").GetAttribute("datetime"));
    }

    /// <summary>Statistics preserve the joined-to-total relationship while rendering active and past totals separately.</summary>
    [Fact]
    public void StatisticsRenderExactQuestCounts()
    {
        var component = Render<QuestHomeStatistics>(parameters => parameters
            .Add(item => item.UpcomingJoinedCount, 2)
            .Add(item => item.UpcomingTotalCount, 7)
            .Add(item => item.ActiveCount, 3)
            .Add(item => item.PastCount, 11));
        Assert.Equal(new[] { "2 / 7", "3", "11" },
            component.FindAll("strong").Select(item => item.TextContent.Trim()));
        Assert.Contains("Upcoming joined / total", component.Markup);
        Assert.Contains("Active now", component.Markup);
        Assert.Contains("Past Quests", component.Markup);
    }

    /// <summary>Compact lists show Event context, linked Quest names, local dates, and a category-specific empty state.</summary>
    [Fact]
    public void QuestListRendersAuthorizedItemsAndEmptyMessage()
    {
        var item = Summary("Active delivery", ParticipationStatus.Joined);
        var component = Render<QuestHomeQuestList>(parameters => parameters
            .Add(section => section.Title, "Happening now")
            .Add(section => section.Items, new[] { item })
            .Add(section => section.EmptyMessage, "Nothing active."));
        Assert.Equal("Parent Event", component.Find(".text-muted").TextContent);
        Assert.Equal("Active delivery", component.Find("a").TextContent);
        Assert.Equal($"/quests/{item.Id}", component.Find("a").GetAttribute("href"));
        Assert.Equal("2026-07-15", component.Find("time").TextContent);

        var empty = Render<QuestHomeQuestList>(parameters => parameters
            .Add(section => section.Title, "Happening now")
            .Add(section => section.Items, Array.Empty<QuestSummary>())
            .Add(section => section.EmptyMessage, "Nothing active."));
        Assert.Contains("Nothing active.", empty.Markup);
        Assert.Empty(empty.FindAll("li"));
    }

    /// <summary>Invitation rows render the requested columns and raise the selected Quest identifier through EventCallback.</summary>
    /// <returns>Completion after the invitation Join action is invoked once.</returns>
    [Fact]
    public async Task InvitationTableRaisesJoinForSelectedQuest()
    {
        var item = Summary("Invited delivery", ParticipationStatus.None);
        Guid? selected = null;
        var component = Render<QuestInvitationTable>(parameters => parameters
            .Add(table => table.Items, new[] { item })
            .Add(table => table.JoinRequested, id => selected = id));
        Assert.Equal(new[] { "Event", "Quest name", "Quest date", "Action" },
            component.FindAll("th").Select(cell => cell.TextContent.Trim()));
        Assert.Equal("Parent Event", component.Find("tbody td").TextContent);
        Assert.Equal($"/quests/{item.Id}", component.Find("tbody a").GetAttribute("href"));
        await component.Find("fluent-button").ClickAsync(new());
        Assert.Equal(item.Id, selected);
    }

    private static QuestSummary Summary(string title, ParticipationStatus participation) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Parent Event", title, "Room",
            new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero),
            "Europe/Prague", QuestStatus.Active, QuestVisibility.Private, 0, 0, null,
            participation, false, "", null);
}
