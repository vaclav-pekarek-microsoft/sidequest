using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Web.Components.Pages.Quests;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Checks the authenticated Quest list's page-size and navigation contract.</summary>
public sealed class QuestListTests : BunitContext
{
    /// <summary>The list requests and advances through exactly twelve Quests per page.</summary>
    /// <returns>Completion after first- and second-page request assertions.</returns>
    [Fact]
    public async Task QuestListUsesTwelveItemPages()
    {
        var requests = new List<PageRequest>();
        Services.AddLogging();
        Services.AddFluentUIComponents();
        Services.AddScoped<ExperienceCoordinator>();
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, arguments) =>
        {
            Assert.Equal(nameof(IEventService.ListAsync), method.Name);
            var page = (PageRequest)arguments![1]!;
            return Task.FromResult(new PageResult<EventSummary>([], 0, page.Page, page.PageSize));
        }));
        Services.AddSingleton(SnapshotServiceProxy.Create<IQuestService>((method, arguments) =>
        {
            Assert.Equal(nameof(IQuestService.ListAsync), method.Name);
            var page = (PageRequest)arguments![2]!;
            requests.Add(page);
            var item = Summary($"Page {page.Page}");
            return Task.FromResult(new PageResult<QuestSummary>([item], 13, page.Page, page.PageSize));
        }));
        SetRendererInfo(new("Server", true));
        var component = Render<QuestList>();
        Assert.Collection(requests, request =>
        {
            Assert.Equal(1, request.Page);
            Assert.Equal(12, request.PageSize);
        });
        await component.InvokeAsync(() => Services.GetRequiredService<ExperienceCoordinator>()
            .ReportConnectionAsync(true, "Europe/Prague"));

        await component.FindAll("fluent-button").Single(button => button.TextContent.Trim() == "Next")
            .ClickAsync(new());

        Assert.All(requests, request => Assert.Equal(12, request.PageSize));
        Assert.Equal(2, requests[^1].Page);
        Assert.Contains("Page 2", component.Markup);
        Assert.True(component.FindAll("fluent-button").Single(button => button.TextContent.Trim() == "Next")
            .HasAttribute("disabled"));
    }

    private static QuestSummary Summary(string title) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Event", title, "Room",
            new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero),
            "Europe/Prague", QuestStatus.Active, QuestVisibility.Public, 0, 0, null,
            ParticipationStatus.None, false, "", null);
}
