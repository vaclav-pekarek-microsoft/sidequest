using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

internal sealed record EventSchedulingScenario(
    SqlTestDatabase Database, EventTestContext Context, Guid EventId, UserAccount Owner, UserAccount Member)
{
    internal static readonly DateTimeOffset End = new(2026, 7, 16, 22, 0, 0, TimeSpan.Zero);

    internal static async Task<EventSchedulingScenario> CreateAsync(SqlTestDatabase database, bool coldLegacyDraft = false)
    {
        var context = new EventTestContext(database);
        var owner = FoundationSeed.NewUser();
        var member = FoundationSeed.NewUser();
        member.TenantId = owner.TenantId;
        await FoundationSeed.PersistAsync(database, owner, member);
        Guid id;
        if (coldLegacyDraft)
        {
            var item = new Event
            {
                CreatorId = owner.Id,
                Name = "New Event",
                Description = "Member-only details",
                DiscoverySummary = "Discovery summary",
                StartDate = new(2026, 7, 15),
                EndDate = new(2026, 7, 16),
                TimeZoneId = "Europe/Prague",
                Status = EventStatus.Draft,
                CreatedUtc = FoundationSeed.Now,
                UpdatedUtc = FoundationSeed.Now
            };
            id = item.Id;
            await FoundationSeed.PersistAsync(database, item,
                new EventOwner { EventId = id, UserId = owner.Id },
                new EventMembership
                {
                    EventId = id, UserId = owner.Id, ChangedById = owner.Id, ChangedUtc = FoundationSeed.Now
                },
                new AuditEntry
                {
                    ResourceId = id, ResourceKind = ResourceKind.Event, ActorId = owner.Id,
                    Action = "Event.Created", Reason = "Event activated; creator assigned as equal owner and member.",
                    OccurredUtc = FoundationSeed.Now, CorrelationId = Guid.NewGuid().ToString("N")
                });
        }
        else
        {
            id = await context.Service(owner).CreateAsync(EventTestContext.Input());
            await using var setup = database.CreateContext();
            (await setup.Events.SingleAsync(x => x.Id == id)).Status = EventStatus.Draft;
            setup.ScheduledWork.RemoveRange(setup.ScheduledWork.Where(x =>
                x.DeduplicationKey.StartsWith($"event.complete.v1:{id:N}:")));
            await setup.SaveChangesAsync();
        }
        await FoundationSeed.PersistAsync(database, new EventMembership
        {
            EventId = id, UserId = member.Id, ChangedById = owner.Id, ChangedUtc = FoundationSeed.Now
        });
        return new(database, context, id, owner, member);
    }

    internal EventService Service(IInterceptor? observer = null, UserAccount? actor = null) =>
        new(observer is null ? Context : new ObservedContextFactory(Database, observer),
            new ResourceAccess(StubCurrentUser.For(actor ?? Owner)), Context.Directory, Context.Quests,
            new ChangeWriter(), Context.Clock, Context.Options);

    internal string Key(DateTimeOffset? end = null) => $"event.complete.v1:{EventId:N}:{(end ?? End).UtcTicks}";

    internal ScheduledWork Work(WorkStatus status, string? key = null, DateTimeOffset? end = null) => new()
    {
        Type = WorkTypes.EventCompletion, DeduplicationKey = key ?? Key(end), Status = status, DueUtc = end ?? End,
        PayloadJson = JsonSerializer.Serialize(new EventCompletionPayload(1, EventId, end ?? End))
    };
}
