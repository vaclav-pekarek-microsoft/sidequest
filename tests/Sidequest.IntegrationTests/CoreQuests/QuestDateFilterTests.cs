using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Checks that start-instant date bounds are applied with authorization before SQL totals and pagination.</summary>
/// <param name="database">Existing isolated migrated-SQL fixture.</param>
public sealed class QuestDateFilterTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>The lower start boundary is inclusive, upper boundary exclusive, and hidden Quests never inflate filtered totals.</summary>
    /// <returns>Completion after exact page IDs/counts, one-sided ranges and compatibility-overload assertions.</returns>
    [Fact]
    public async Task DateRange_FiltersBeforeCountsAndPages_WithoutPrivateHints()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var before = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        before.StartUtc = FoundationSeed.Now.AddMinutes(-1);
        var inside = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        inside.StartUtc = FoundationSeed.Now.AddMinutes(30);
        var upper = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        upper.StartUtc = FoundationSeed.Now.AddHours(1);
        var hidden = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        hidden.StartUtc = FoundationSeed.Now.AddMinutes(45);
        hidden.Visibility = QuestVisibility.Private;
        await FoundationSeed.PersistAsync(database, before, inside, upper, hidden);
        var service = scenario.Service(scenario.Seed.Other);
        var range = new QuestDateFilter(FoundationSeed.Now, FoundationSeed.Now.AddHours(1));
        var first = await service.ListAsync(QuestListKind.Discover, scenario.Seed.Event.Id, new PageRequest(1, 1), range);
        var second = await service.ListAsync(QuestListKind.Discover, scenario.Seed.Event.Id, new PageRequest(2, 1), range);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(2, second.TotalCount);
        Assert.Equal(scenario.Seed.Quest.Id, Assert.Single(first.Items).Id);
        Assert.Equal(inside.Id, Assert.Single(second.Items).Id);
        var lowerOnly = await service.ListAsync(QuestListKind.Discover, null, new(), new QuestDateFilter(FoundationSeed.Now));
        Assert.Equal(new[] { scenario.Seed.Quest.Id, inside.Id, upper.Id }, lowerOnly.Items.Select(q => q.Id).ToArray());
        var upperOnly = await service.ListAsync(QuestListKind.Discover, null, new(), new QuestDateFilter(UntilUtc: FoundationSeed.Now.AddHours(1)));
        Assert.Equal(new[] { before.Id, scenario.Seed.Quest.Id, inside.Id }, upperOnly.Items.Select(q => q.Id).ToArray());
        var original = await service.ListAsync(QuestListKind.Discover, null, new());
        var unbounded = await service.ListAsync(QuestListKind.Discover, null, new(), new QuestDateFilter());
        Assert.Equal(4, unbounded.TotalCount);
        Assert.Equal(original.TotalCount, unbounded.TotalCount);
        Assert.Equal(1, unbounded.Page);
        Assert.Equal(25, unbounded.PageSize);
        Assert.Equal(original.Items.Select(q => q.Id), unbounded.Items.Select(q => q.Id));
    }

    /// <summary>Equal and reversed UTC instant bounds fail explicitly rather than producing success-shaped empty pages.</summary>
    /// <param name="deltaTicks">Nonpositive displacement of the upper boundary from the lower boundary.</param>
    /// <returns>Completion after precise validation metadata assertions.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DateRange_RejectsNonIncreasingBounds(long deltaTicks)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Service().ListAsync(
            QuestListKind.Discover, null, new(), new QuestDateFilter(FoundationSeed.Now, FoundationSeed.Now.AddTicks(deltaTicks))));
        Assert.Equal(ErrorCode.Validation, failure.Code);
        Assert.Equal("Dates", failure.Field);
    }
}
