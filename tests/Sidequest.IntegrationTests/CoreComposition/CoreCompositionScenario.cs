using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Notifications;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Quests;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;
using Sidequest.Infrastructure.Persistence;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.CoreEvents;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreComposition;

internal sealed class CoreCompositionScenario : IAsyncDisposable
{
    private readonly SqlTestDatabase database = new();
    private readonly StubCurrentUser current = new(null);
    internal DeliveryClock Clock { get; } = new(FoundationSeed.Now);
    internal RecordingEmailGateway Gateway { get; } = new();
    internal EventDirectoryStub Directory { get; } = new();
    internal WorkExecutionContext Execution { get; } = new();
    internal DurableWorkOptions Options { get; } = new();
    internal EventService Events { get; }
    internal QuestService Quests { get; }
    internal NotificationService Notifications { get; }
    internal SqlWorkQueue Queue { get; }
    internal DurableWorkRunner Runner { get; }
    internal IBackgroundWorkHandler[] Handlers { get; }
    internal UserAccount Manager { get; private set; } = null!;
    internal Guid EventId { get; private set; }

    private CoreCompositionScenario()
    {
        var factory = new ScenarioContextFactory(database);
        var access = new ResourceAccess(current);
        var writer = new ChangeWriter();
        var lifecycle = new QuestEventLifecycle(writer);
        var reconciler = new EventLifecycleReconciler(lifecycle);
        var policy = new RecipientPolicy();
        var scheduler = new ReminderScheduler(Clock);
        var renderer = new RecipientCalendarRenderer(new EmailDeliveryOptions { SenderAddress = "organizer@example.invalid" });
        Events = new(factory, access, Directory, lifecycle, writer, Clock, new EventOperationOptions());
        Quests = new(factory, access, writer, Clock, reconciler);
        Notifications = new(factory, access, policy, scheduler, renderer, Clock);
        Queue = new(factory, Clock, Options);
        var delivery = new DeliveryDispatcher(factory, policy, renderer, Gateway, Clock, Options);
        Handlers =
        [
            new ChangeOutboxHandler(factory, policy, scheduler, Execution, Clock),
            new ReminderWorkHandler(factory, policy, Execution, Options, Clock),
            new EventCompletionHandler(factory, lifecycle, Clock),
            new QuestCompletionHandler(factory, writer, Clock, reconciler),
            new BulkMembershipHandler(factory, Directory, writer, lifecycle, Clock, new EventOperationOptions())
        ];
        Runner = new(Queue, Handlers, delivery, Execution, Clock, Options, NullLogger<DurableWorkRunner>.Instance);
    }

    internal static async Task<CoreCompositionScenario> CreateAsync(bool publish = true)
    {
        var scenario = new CoreCompositionScenario();
        var initialized = false;
        try
        {
            await scenario.database.InitializeAsync();
            scenario.Manager = await scenario.AddUserAsync("manager", registered: true, member: false);
            scenario.ActAs(scenario.Manager);
            scenario.EventId = await scenario.Events.CreateAsync(EventInput());
            if (publish)
                await scenario.Events.ChangeStatusAsync(scenario.EventId, await scenario.EventVersionAsync(),
                    EventStatus.Active, "");
            initialized = true;
            return scenario;
        }
        finally
        {
            if (!initialized)
                await scenario.DisposeAsync();
        }
    }

    internal SidequestDbContext Read() => database.CreateContext();

    internal void ActAs(UserAccount user) => current.Identity = StubCurrentUser.For(user).Identity;

    internal async Task<UserAccount> AddUserAsync(string name, bool registered = false, bool member = true)
    {
        var user = FoundationSeed.NewUser();
        user.TenantId = Manager?.TenantId ?? Guid.NewGuid();
        user.Email = $"{name}@example.invalid";
        user.DisplayName = name;
        user.LastSignedInUtc = registered ? Clock.Now : null;
        await using var db = Read();
        db.Users.Add(user);
        if (member)
            db.EventMemberships.Add(new EventMembership
            {
                EventId = EventId, UserId = user.Id, Status = MembershipStatus.Active,
                ChangedById = Manager!.Id, ChangedUtc = Clock.Now
            });
        await db.SaveChangesAsync();
        return user;
    }

    internal static EventInput EventInput(int endDay = 16) =>
        new("Composition Event", "Event secret", "Safe discovery", new(2026, 7, 15),
            new(2026, 7, endDay), "Europe/Prague");

    internal static QuestInput QuestInput(QuestVisibility visibility = QuestVisibility.Public,
        int startHour = 13, int endHour = 15, string location = "Private room sentinel") =>
        new("Private title sentinel", "Private description sentinel", location, 12,
            new(2026, 7, 15, startHour, 0, 0, DateTimeKind.Unspecified),
            new(2026, 7, 15, endHour, 0, 0, DateTimeKind.Unspecified), null, null, visibility);

    internal async Task<Guid> CreateQuestAsync(UserAccount owner, QuestVisibility visibility = QuestVisibility.Public,
        bool publish = true, int startHour = 13, int endHour = 15)
    {
        ActAs(owner);
        var id = await Quests.CreateAsync(EventId, QuestInput(visibility, startHour, endHour));
        if (publish)
            await Quests.ChangeStatusAsync(id, await QuestVersionAsync(id), QuestStatus.Active, "");
        return id;
    }

    internal async Task ParticipateAsync(Guid questId, UserAccount user, ParticipationCommand command)
    {
        ActAs(user);
        await Quests.ParticipateAsync(questId, command);
    }

    internal async Task<string> EventVersionAsync()
    {
        await using var db = Read();
        return Convert.ToBase64String((await db.Events.SingleAsync(x => x.Id == EventId)).Version);
    }

    internal async Task<string> QuestVersionAsync(Guid id) => Convert.ToBase64String((await QuestAsync(id)).Version);

    internal async Task<Quest> QuestAsync(Guid id)
    {
        await using var db = Read();
        return await db.Quests.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    internal async Task<OutboxMessage[]> OutboxAsync()
    {
        await using var db = Read();
        return await db.OutboxMessages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
    }

    internal async Task<ScheduledWork> WorkAsync(Guid id)
    {
        await using var db = Read();
        return await db.ScheduledWork.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    internal async Task<ScheduledWork[]> ScheduledAsync(string type)
    {
        await using var db = Read();
        return await db.ScheduledWork.AsNoTracking().Where(x => x.Type == type).OrderBy(x => x.Id).ToArrayAsync();
    }

    internal async Task SetDueAsync(Guid id, DateTimeOffset due)
    {
        await using var db = Read();
        (await db.ScheduledWork.SingleAsync(x => x.Id == id)).DueUtc = due;
        await db.SaveChangesAsync();
    }

    internal async Task DrainAsync(string category)
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            if (!await Runner.RunOnceAsync(category))
            {
                Assert.Null(Execution.Lease);
                await using var db = Read();
                if (category == "outbox")
                    Assert.Empty(await db.OutboxMessages.Where(x => x.Status != WorkStatus.Completed).ToArrayAsync());
                if (category == "delivery")
                    Assert.Empty(await db.NotificationDeliveries.Where(x =>
                        x.Status != WorkStatus.Completed && x.Status != WorkStatus.Superseded).ToArrayAsync());
                return;
            }
        }
        Assert.Fail($"The bounded {category} drain exceeded 100 claims.");
    }

    internal async Task FlushAsync()
    {
        await DrainAsync("outbox");
        await DrainAsync("delivery");
    }

    internal static T Payload<T>(string json) where T : class =>
        Assert.IsType<T>(JsonSerializer.Deserialize<T>(json));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await database.DisposeAsync();
}
