using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Verifies strict editable Event dates without changing legacy inclusive-date reads or publication.</summary>
/// <param name="database">Fixture-owned, migrated SQL database isolated from application configuration.</param>
public sealed class EventDateValidationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Accepts only a later end date at the create boundary, with no aggregate or audit effects for rejected input.</summary>
    /// <param name="endOffsetDays">Whole-day distance from the start date to the proposed inclusive end date.</param>
    /// <returns>Completion after checking the validation field and persisted aggregate or absence of writes.</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CreateRequiresStrictlyLaterEndWithoutPartialWrites(int endOffsetDays)
    {
        var context = new EventTestContext(database);
        var user = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, user);
        var input = EventTestContext.Input();
        input = input with { EndDate = input.StartDate.AddDays(endOffsetDays) };
        var service = context.Service(user);
        if (endOffsetDays <= 0)
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(input));
            Assert.Equal(ErrorCode.Validation, error.Code);
            Assert.Equal("EndDate", error.Field);
            Assert.Equal("End date must be after start date.", error.Message);
            await using var read = database.CreateContext();
            Assert.False(await read.Events.AnyAsync(x => x.CreatorId == user.Id));
            Assert.False(await read.AuditEntries.AnyAsync(x => x.ActorId == user.Id));
            Assert.False(await read.EventMemberships.AnyAsync(x => x.UserId == user.Id));
        }
        else
        {
            var id = await service.CreateAsync(input);
            var saved = await service.GetAsync(id);
            Assert.Equal(input.StartDate, saved.Summary.StartDate);
            Assert.Equal(input.EndDate, saved.Summary.EndDate);
            Assert.Equal(EventStatus.Draft, saved.Summary.Status);
            Assert.True(saved.Summary.IsOwner);
        }
    }

    /// <summary>Rejects same-day and reversed edits atomically while accepting the adjacent later end date with the supplied version.</summary>
    /// <param name="endOffsetDays">Whole-day distance between proposed dates.</param>
    /// <returns>Completion after checking unchanged state on rejection or the exact committed edit and audit.</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EditRequiresStrictlyLaterEndAndPreservesRejectedState(int endOffsetDays)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var service = context.Service(seed.User);
        var before = await service.GetAsync(seed.Event.Id);
        var input = EventTestContext.Input("Changed date configuration");
        input = input with { EndDate = input.StartDate.AddDays(endOffsetDays) };
        if (endOffsetDays <= 0)
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                service.EditAsync(seed.Event.Id, before.Summary.Version, input));
            Assert.Equal(ErrorCode.Validation, error.Code);
            Assert.Equal("EndDate", error.Field);
            Assert.Equal("End date must be after start date.", error.Message);
            var after = await service.GetAsync(seed.Event.Id);
            Assert.Equal(before.Summary.Version, after.Summary.Version);
            Assert.Equal(before.Summary.Name, after.Summary.Name);
            Assert.Equal(before.Summary.EndDate, after.Summary.EndDate);
            await using var read = database.CreateContext();
            Assert.False(await read.AuditEntries.AnyAsync(x => x.ResourceId == seed.Event.Id));
        }
        else
        {
            await service.EditAsync(seed.Event.Id, before.Summary.Version, input);
            var after = await service.GetAsync(seed.Event.Id);
            Assert.Equal(input.Name, after.Summary.Name);
            Assert.Equal(input.EndDate, after.Summary.EndDate);
            Assert.NotEqual(before.Summary.Version, after.Summary.Version);
            await using var read = database.CreateContext();
            Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Event.Edited").ToListAsync());
        }
    }

    /// <summary>Retains the old single-day window for authorized viewing and the existing publication lifecycle without rewriting stored dates.</summary>
    /// <returns>Completion after reading, publishing, and verifying the unchanged inclusive one-day UTC window.</returns>
    [Fact]
    public async Task LegacySingleDayEventRemainsReadableAndPublishable()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        await using (var setup = database.CreateContext())
        {
            var item = await setup.Events.SingleAsync(x => x.Id == seed.Event.Id);
            item.Status = EventStatus.Draft;
            item.EndDate = item.StartDate;
            await setup.SaveChangesAsync();
        }
        var service = context.Service(seed.User);
        var before = await service.GetAsync(seed.Event.Id);
        Assert.Equal(before.Summary.StartDate, before.Summary.EndDate);
        await service.ChangeStatusAsync(seed.Event.Id, before.Summary.Version, EventStatus.Active, "");
        var after = await service.GetAsync(seed.Event.Id);
        Assert.Equal(EventStatus.Active, after.Summary.Status);
        Assert.Equal(before.Summary.StartDate, after.Summary.StartDate);
        Assert.Equal(before.Summary.EndDate, after.Summary.EndDate);
        var window = TimeRules.EventWindow(after.Summary.StartDate, after.Summary.EndDate, after.Summary.TimeZoneId);
        Assert.Equal(TimeSpan.FromDays(1), window.End - window.Start);
    }
}
