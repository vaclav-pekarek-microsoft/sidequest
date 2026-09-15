using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Experience;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Experience;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Tests dashboard orchestration at its real application boundary, including asynchronous loading and access loss after reconnect.</summary>
public sealed class QuestDashboardTests : BunitContext
{
    /// <summary>Stubs only the browser bridge; actual dashboard, filters, cards and projection orchestration remain under test.</summary>
    public QuestDashboardTests()
    {
        ComponentFactories.AddStub<ConnectionStatus>();
        Services.AddLogging();
        Services.AddScoped<ExperienceCoordinator>();
    }

    /// <summary>A delayed authorized page shows loading; reconnect reauthorization removes previously displayed protected content on denial.</summary>
    /// <returns>Completion after loading, exact content, cleared-result and safe-error assertions.</returns>
    [Fact]
    public async Task ReconnectReauthorizationClearsProtectedCardsAndRendersFailure()
    {
        var pending = new TaskCompletionSource<PageResult<QuestSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deny = false;
        var calls = 0;
        Register(() =>
        {
            calls++;
            return deny ? Task.FromException<PageResult<QuestSummary>>(new DomainException(ErrorCode.NotFound, "PRIVATE FAILURE")) : pending.Task;
        });
        var component = Render<QuestDashboard>();
        Assert.Contains("Loading authorized Quests", component.Markup);
        var quest = new QuestSummary(Guid.NewGuid(), Guid.NewGuid(), "Event", "AUTHORIZED TITLE", "Room",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "Etc/UTC", QuestStatus.Active,
            QuestVisibility.Public, 2, 1, null, ParticipationStatus.Joined, false, false, "", null);
        pending.SetResult(new([quest], 1, 1, 25));
        component.WaitForAssertion(() => Assert.Contains("AUTHORIZED TITLE", component.Markup));
        deny = true;
        await component.InvokeAsync(() => Services.GetRequiredService<ExperienceCoordinator>().ReportConnectionAsync(true, "Europe/Prague"));
        component.WaitForAssertion(() => Assert.Contains("unavailable or access has changed", component.Find("[role=alert]").TextContent));
        Assert.DoesNotContain("AUTHORIZED TITLE", component.Markup);
        Assert.DoesNotContain("PRIVATE FAILURE", component.Markup);
        Assert.Equal(2, calls);
    }

    /// <summary>An authorized empty result is distinct from an error, retains Invited navigation and disables next-page navigation.</summary>
    [Fact]
    public void EmptyDashboardShowsUsefulNavigationWithoutFabricatedCards()
    {
        Register(() => Task.FromResult(new PageResult<QuestSummary>([], 0, 1, 25)));
        var component = Render<QuestDashboard>();
        Assert.Contains("No Quests in this view", component.Markup);
        Assert.Empty(component.FindAll("[role=alert]"));
        Assert.Empty(component.FindAll("article"));
        Assert.Equal("/quests?view=Invited", component.Find("a[href*='Invited']").GetAttribute("href"));
        Assert.True(component.FindAll("button").Single(b => b.TextContent == "Next Quests").HasAttribute("disabled"));
    }

    private void Register(Func<Task<PageResult<QuestSummary>>> list)
    {
        Services.AddSingleton(SnapshotServiceProxy.Create<IQuestService>((method, _) =>
            method.Name == nameof(IQuestService.ListAsync) ? list() : throw new NotSupportedException()));
        Services.AddSingleton(SnapshotServiceProxy.Create<IEventService>((method, _) =>
            method.Name == nameof(IEventService.ListAsync) ?
                Task.FromResult(new PageResult<EventSummary>([], 0, 1, 100)) : throw new NotSupportedException()));
        Services.AddSingleton(SnapshotServiceProxy.Create<ISidequestDbContextFactory>((_, _) => throw new NotSupportedException("No per-card database query is expected.")));
        Services.AddSingleton(SnapshotServiceProxy.Create<IResourceAccess>((_, _) => throw new NotSupportedException("No owned private statistics are requested.")));
        Services.AddScoped<DashboardService>();
        SetRendererInfo(new("Server", true));
    }
}
