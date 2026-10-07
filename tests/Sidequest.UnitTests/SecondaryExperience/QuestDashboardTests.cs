using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Experience;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Experience;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Tests signed-in home orchestration, reconnect reauthorization, and invitation participation.</summary>
public sealed class QuestDashboardTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Registers the real home projection and per-circuit coordinator for each isolated renderer.</summary>
    public QuestDashboardTests()
    {
        Services.AddLogging();
        Services.AddFluentUIComponents();
        Services.AddScoped<ExperienceCoordinator>();
        Services.AddSingleton<TimeProvider>(new FixedClock(Now));
    }

    /// <summary>A delayed authorized projection shows loading; reconnect denial removes previously displayed protected content.</summary>
    /// <returns>Completion after loading, exact content, cleared-result, and failure assertions.</returns>
    [Fact]
    public async Task ReconnectReauthorizationClearsProtectedQuestDataOnFailure()
    {
        var pending = new TaskCompletionSource<PageResult<QuestSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deny = false;
        var boardCalls = 0;
        Register((kind, _) =>
        {
            if (kind == QuestListKind.Invited)
                return Task.FromResult(Page());
            boardCalls++;
            return deny
                ? Task.FromException<PageResult<QuestSummary>>(new DomainException(ErrorCode.NotFound, "Quest data is unavailable or access has changed."))
                : pending.Task;
        });

        var component = Render<QuestDashboard>();
        Assert.Contains("Loading Quest overview", component.Markup);
        var quest = Summary("AUTHORIZED TITLE", Now.AddHours(-1), Now.AddHours(1), ParticipationStatus.Joined);
        pending.SetResult(Page(quest));
        component.WaitForAssertion(() => Assert.Contains("AUTHORIZED TITLE", component.Markup));

        deny = true;
        await component.InvokeAsync(() => Services.GetRequiredService<ExperienceCoordinator>()
            .ReportConnectionAsync(true, "Europe/Prague"));

        component.WaitForAssertion(() =>
        {
            Assert.Contains("unavailable or access has changed", component.Find("[role=alert]").TextContent);
            Assert.DoesNotContain("AUTHORIZED TITLE", component.Markup);
        });
        Assert.Equal(2, boardCalls);
    }

    /// <summary>The home renders exact statistics, all current Quests, top upcoming categories, and no filter or shortcut controls.</summary>
    [Fact]
    public void HomeRendersFocusedOverviewWithoutDashboardFilters()
    {
        var active = Summary("Happening now", Now.AddMinutes(-30), Now.AddMinutes(30), ParticipationStatus.None);
        var joined = Summary("Joined next", Now.AddHours(1), Now.AddHours(2), ParticipationStatus.Joined);
        var followed = Summary("Following next", Now.AddHours(2), Now.AddHours(3), ParticipationStatus.Following);
        var past = Summary("Already done", Now.AddHours(-3), Now.AddHours(-2), ParticipationStatus.Joined) with
        {
            Status = QuestStatus.Completed
        };
        Register((kind, _) => Task.FromResult(kind == QuestListKind.Board
            ? Page(active, joined, followed, past)
            : Page()));

        var component = Render<QuestDashboard>();

        Assert.Equal(new[] { "1 / 2", "1", "1" },
            component.FindAll(".quest-home-statistics strong").Select(item => item.TextContent.Trim()));
        Assert.Contains("Happening now", component.Markup);
        Assert.Contains("Joined next", component.Markup);
        Assert.Contains("Following next", component.Markup);
        Assert.DoesNotContain("Find your Quests", component.Markup);
        Assert.DoesNotContain("Dashboard shortcuts", component.Markup);
        Assert.Empty(component.FindAll("select"));
        Assert.Empty(component.FindAll("[style]"));
    }

    /// <summary>Joining an invitation executes the Quest command, reloads the projection, and removes the completed invitation row.</summary>
    /// <returns>Completion after one Join command and refreshed invitation assertions.</returns>
    [Fact]
    public async Task InvitationJoinReloadsHomeAndRemovesInvitation()
    {
        var invitation = Summary("Invitation", Now.AddHours(1), Now.AddHours(2), ParticipationStatus.None);
        var joined = false;
        Register(
            (kind, _) => Task.FromResult(kind switch
            {
                QuestListKind.Board when joined => Page(invitation with { Participation = ParticipationStatus.Joined }),
                QuestListKind.Board => Page(invitation),
                QuestListKind.Invited when !joined => Page(invitation),
                QuestListKind.Invited => Page(),
                _ => throw new NotSupportedException()
            }),
            questId =>
            {
                Assert.Equal(invitation.Id, questId);
                joined = true;
                return Task.CompletedTask;
            });
        await Services.GetRequiredService<ExperienceCoordinator>().ReportConnectionAsync(true, "Europe/Prague");
        var component = Render<QuestDashboard>();

        await component.Find("fluent-button").ClickAsync(new());

        component.WaitForAssertion(() =>
        {
            Assert.Contains("No Quest invitations need your response.", component.Markup);
            Assert.DoesNotContain("<tbody>", component.Markup);
        });
        Assert.True(joined);
        Assert.Contains("Invitation", component.Markup);
    }

    private void Register(
        Func<QuestListKind, PageRequest, Task<PageResult<QuestSummary>>> list,
        Func<Guid, Task>? participate = null)
    {
        Services.AddSingleton(SnapshotServiceProxy.Create<IQuestService>((method, arguments) =>
        {
            if (method.Name == nameof(IQuestService.ListAsync))
                return list((QuestListKind)arguments![0]!, (PageRequest)arguments[2]!);
            if (method.Name == nameof(IQuestService.ParticipateAsync) && participate is not null)
                return participate((Guid)arguments![0]!);
            throw new NotSupportedException(method.Name);
        }));
        Services.AddScoped<QuestHomeService>();
        SetRendererInfo(new("Server", true));
    }

    private static PageResult<QuestSummary> Page(params QuestSummary[] items) =>
        new(items, items.Length, 1, 100);

    private static QuestSummary Summary(
        string title,
        DateTimeOffset start,
        DateTimeOffset end,
        ParticipationStatus participation) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Parent Event", title, "Room", start, end, "Europe/Prague",
            QuestStatus.Active, QuestVisibility.Public, 0, 0, null, participation, false, true, "", null);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => now;
    }
}
