using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

internal sealed class DeliveryScenario : IAsyncDisposable
{
    internal SqlTestDatabase Database { get; } = new();
    internal DeliveryClock Clock { get; } = new(FoundationSeed.Now);
    internal FoundationSeed Seed { get; private set; } = default!;
    internal ScenarioContextFactory Factory { get; private set; } = default!;
    internal StubCurrentUser CurrentUser { get; private set; } = default!;
    internal RecipientPolicy Policy { get; } = new();
    internal WorkExecutionContext Execution { get; } = new();
    internal DurableWorkOptions Options { get; } = new();
    internal RecipientCalendarRenderer Renderer { get; } = new(new EmailDeliveryOptions { SenderAddress = "organizer@example.invalid" });
    internal SqlWorkQueue Queue => new(Factory, Clock, Options);
    internal NotificationService Service => new(Factory, new ResourceAccess(CurrentUser), Policy, new(Clock), Renderer, Clock);
    internal ChangeOutboxHandler Changes => new(Factory, Policy, new(Clock), Execution, Clock);
    internal ReminderWorkHandler Reminders => new(Factory, Policy, Execution, Options, Clock);

    internal static async Task<DeliveryScenario> CreateAsync()
    {
        var result = new DeliveryScenario();
        await result.Database.InitializeAsync();
        result.Factory = new(result.Database);
        result.Seed = await FoundationSeed.CreateAsync(result.Database);
        result.CurrentUser = StubCurrentUser.For(result.Seed.User);
        await using var db = result.Database.CreateContext();
        var quest = await db.Quests.FindAsync(result.Seed.Quest.Id);
        quest!.StartUtc = FoundationSeed.Now.AddHours(1);
        quest.EndUtc = FoundationSeed.Now.AddHours(2);
        quest.StartRevision = 1;
        db.EventMemberships.Add(result.Seed.Membership());
        db.Participations.Add(new QuestParticipation
        {
            QuestId = quest.Id, UserId = result.Seed.User.Id,
            Status = ParticipationStatus.Joined, ChangedUtc = FoundationSeed.Now
        });
        await db.SaveChangesAsync();
        return result;
    }

    internal async Task<Guid> AddChangeAsync(NotificationKind kind = NotificationKind.Joined, long revision = 7)
    {
        var id = Guid.NewGuid();
        var change = new ChangeEnvelope(id, kind, Seed.Event.Id, Seed.Quest.Id, Seed.User.Id,
            [Seed.User.Id], Clock.GetUtcNow(), revision, PreviousAttendeeIds: [Seed.User.Id], CalendarChanged: true,
            AffectedUserIds: [Seed.User.Id]);
        await AddChangeAsync(change);
        return id;
    }

    internal async Task AddChangeAsync(ChangeEnvelope change)
    {
        await using var db = Database.CreateContext();
        new ChangeWriter().Append(db, change);
        await db.SaveChangesAsync();
    }

    internal async Task ProcessChangeAsync()
    {
        var lease = await Queue.ClaimAsync("outbox");
        Assert.NotNull(lease);
        Execution.Lease = lease;
        await Changes.ExecuteAsync(lease.Id, CancellationToken.None);
        await Queue.CompleteAsync(lease);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(Database.DisposeAsync());
}
