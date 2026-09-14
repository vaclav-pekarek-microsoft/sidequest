using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Verifies validation and failed durable-intent paths leave the SQL aggregate and associated records unchanged.</summary>
/// <param name="database">Fixture-owned isolated migrated catalog.</param>
public sealed class QuestFailureTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>A delivery-intent staging failure rolls back attendance, calendar revision and audit; it never pretends the mutation succeeded.</summary>
    /// <returns>Completion after propagated error and exact absence/state assertions.</returns>
    [Fact]
    public async Task OutboxFailure_RollsBackParticipationCalendarAndAudit()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = new QuestService(new QuestTestFactory(database), new ResourceAccess(StubCurrentUser.For(scenario.Seed.User)),
            new RejectingChangeWriter(), scenario.Clock, scenario.Reconciler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join));
        Assert.Equal("Synthetic durable-intent staging failure.", error.Message);
        await using var db = database.CreateContext();
        Assert.Equal(7, (await db.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id)).CalendarRevision);
        Assert.False(await db.Participations.AnyAsync(p => p.QuestId == scenario.Seed.Quest.Id));
        Assert.False(await db.AuditEntries.AnyAsync(p => p.ResourceId == scenario.Seed.Quest.Id));
        Assert.False(await db.OutboxMessages.AnyAsync(p => p.AggregateId == scenario.Seed.Quest.Id));
    }

    /// <summary>Server validation rejects each independent malformed content partition before any draft or owner assignment is saved.</summary>
    /// <param name="invalid">Invalid content, capacity, interval, or enum partition.</param>
    /// <returns>Completion after Validation error and unchanged database assertions.</returns>
    [Theory]
    [InlineData("title-short")]
    [InlineData("title-long")]
    [InlineData("description")]
    [InlineData("location")]
    [InlineData("capacity-zero")]
    [InlineData("capacity-negative")]
    [InlineData("capacity-over")]
    [InlineData("interval-equal")]
    [InlineData("interval-reversed")]
    [InlineData("visibility")]
    public async Task InvalidConfiguration_CreatesNoPartialDraft(string invalid)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var input = scenario.Input();
        input = invalid switch
        {
            "title-short" => input with { Title = " ab " },
            "title-long" => input with { Title = new string('a', 121) },
            "description" => input with { Description = new string('a', 10001) },
            "location" => input with { Location = new string('a', 501) },
            "capacity-zero" => input with { SuggestedCapacity = 0 },
            "capacity-negative" => input with { SuggestedCapacity = -1 },
            "capacity-over" => input with { SuggestedCapacity = 10001 },
            "interval-equal" => input with { EndLocal = input.StartLocal },
            "interval-reversed" => input with { EndLocal = input.StartLocal.AddTicks(-1) },
            _ => input with { Visibility = (QuestVisibility)999 }
        };
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().CreateAsync(scenario.Seed.Event.Id, input))).Code);
        await using var db = database.CreateContext();
        Assert.Single(await db.Quests.Where(q => q.EventId == scenario.Seed.Event.Id).ToListAsync());
        Assert.False(await db.AuditEntries.AnyAsync(a => a.ActorId == scenario.Seed.User.Id));
    }

    /// <summary>A draft may omit location, but publication must not partially activate it or schedule delivery.</summary>
    /// <returns>Completion after location validation and complete draft-state assertions.</returns>
    [Fact]
    public async Task PublishWithoutLocation_RemainsUnpublished()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = await service.CreateAsync(scenario.Seed.Event.Id, scenario.Input() with { Location = " " });
        var summary = (await service.GetAsync(id)).Summary;
        var failure = await Assert.ThrowsAsync<DomainException>(() => service.ChangeStatusAsync(id, summary.Version, QuestStatus.Active, ""));
        Assert.Equal(ErrorCode.Validation, failure.Code);
        Assert.Equal("Location", failure.Field);
        await using var db = database.CreateContext();
        Assert.Equal(QuestStatus.Draft, (await db.Quests.SingleAsync(q => q.Id == id)).Status);
        Assert.False(await db.QuestStatusHistory.AnyAsync(q => q.QuestId == id));
        Assert.False(await db.OutboxMessages.AnyAsync(q => q.AggregateId == id));
        Assert.False(await db.ScheduledWork.AnyAsync(q => q.QuestId == id));
    }

    /// <summary>An ineligible additional owner does not satisfy continuity when the last eligible owner attempts removal.</summary>
    /// <returns>Completion after explicit conflict and unchanged equal-owner relations.</returns>
    [Fact]
    public async Task LastEligibleOwner_CannotLeaveOnlyDepartedOwners()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        await service.AddOwnerAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id);
        await using (var db = database.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == scenario.Seed.Other.Id)).DepartureVerifiedUtc = scenario.Clock.Now;
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            service.RemoveOwnerAsync(scenario.Seed.Quest.Id, scenario.Seed.User.Id))).Code);
        await using var read = database.CreateContext();
        Assert.Equal(2, await read.QuestOwners.CountAsync(q => q.QuestId == scenario.Seed.Quest.Id));
        Assert.Equal(scenario.Seed.User.Id, Assert.Single((await service.GetAsync(scenario.Seed.Quest.Id)).Owners).Id);
    }
}
