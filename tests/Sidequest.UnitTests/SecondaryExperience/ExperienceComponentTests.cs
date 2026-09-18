using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Web.Components.Experience;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Checks reusable time, filter and card rendering with real bUnit components and no browser-side authorization assumptions.</summary>
public sealed class ExperienceComponentTests : BunitContext
{
    /// <summary>Registers the real per-circuit experience state for each isolated renderer.</summary>
    public ExperienceComponentTests() => Services.AddScoped<ExperienceCoordinator>();

    /// <summary>Prerendered time uses an explicit fallback; detecting a different browser zone adds a secondary equivalent without changing primary Event time.</summary>
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

    /// <summary>Only owner cards render private invitation totals; nullable counts are not fabricated as zero.</summary>
    /// <param name="owner">Whether the authorized summary grants ownership.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvitationCountIsOwnerOnly(bool owner)
    {
        var summary = new QuestSummary(Guid.NewGuid(), Guid.NewGuid(), "Event", "Quest title", "Room",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), "Etc/UTC",
            QuestStatus.Active, QuestVisibility.Private, 3, 2, null, ParticipationStatus.None, owner, false, "", null);
        var component = Render<DashboardQuestCard>(p => p.Add(c => c.Item, summary).Add(c => c.InvitedCount, 7));
        Assert.Equal(owner, component.Markup.Contains("7 invited", StringComparison.Ordinal));
        Assert.Contains("3 joined", component.Markup);
        Assert.Contains("2 following", component.Markup);
        Assert.Equal("https://placehold.co/600x400?text=Quest%20title", component.Find("img").GetAttribute("src"));
    }

    /// <summary>Filters exclude Quest category navigation, retain explicit date semantics, and remain unavailable while disconnected.</summary>
    [Fact]
    public void FiltersExcludeQuestViewAndHaveExplicitUtcDateSemantics()
    {
        var component = Render<DashboardFilters>(p => p.Add(c => c.Disabled, true));
        Assert.True(component.Find("fieldset").HasAttribute("disabled"));
        Assert.Equal("Filter Quests", component.Find("legend").TextContent);
        Assert.DoesNotContain("View", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Inclusive start dates in UTC (across Events)", component.Markup);
        Assert.Equal(new[] { "25", "50", "100" },
            component.FindAll("select")[1].QuerySelectorAll("option").Select(o => o.GetAttribute("value")));
    }
}
