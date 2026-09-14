using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

internal sealed class EventTestContext(SqlTestDatabase database) : ISidequestDbContextFactory
{
    internal FixedClock Clock { get; } = new();
    internal EventDirectoryStub Directory { get; } = new();
    internal RecordingQuestLifecycle Quests { get; } = new();
    internal EventOperationOptions Options { get; init; } = new();

    internal EventService Service(UserAccount actor) => new(this, new ResourceAccess(StubCurrentUser.For(actor)),
        Directory, Quests, new ChangeWriter(), Clock, Options);

    internal EventCompletionHandler Completion() => new(this, Quests, Clock);
    internal BulkMembershipHandler Bulk() => new(this, Directory, new ChangeWriter(), Quests, Clock, Options);

    internal static EventInput Input(string name = "  New Event  ") =>
        new(name, "Member-only details", "Discovery summary", new(2026, 7, 15), new(2026, 7, 16), "Europe/Prague");

    internal async Task<FoundationSeed> SeedAsync()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await using var db = database.CreateContext();
        var target = await db.Users.FindAsync(seed.Other.Id);
        target!.TenantId = seed.User.TenantId;
        seed.Other.TenantId = seed.User.TenantId;
        db.EventOwners.Add(new EventOwner { EventId = seed.Event.Id, UserId = seed.User.Id });
        db.EventMemberships.Add(seed.Membership());
        await db.SaveChangesAsync();
        Directory.Users.Add(seed.Other.ObjectId, new(seed.User.TenantId, seed.Other.ObjectId,
            seed.Other.DisplayName, seed.Other.Email, true));
        return seed;
    }

    /// <inheritdoc />
    public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ISidequestDbContext>(database.CreateContext());
    }

    internal sealed class FixedClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = FoundationSeed.Now;
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
