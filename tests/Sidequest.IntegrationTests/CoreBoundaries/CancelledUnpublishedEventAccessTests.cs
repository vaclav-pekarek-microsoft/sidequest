using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreBoundaries;

/// <summary>Verifies retained Event draft privacy and its inheritance by child authorization using real SQL facts.</summary>
/// <param name="database">The existing fixture owning an isolated migrated catalog.</param>
public sealed class CancelledUnpublishedEventAccessTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Gets cancelled/archived, published/unpublished, and independent role combinations.</summary>
    public static TheoryData<EventStatus, bool, string> AccessCases
    {
        get
        {
            var data = new TheoryData<EventStatus, bool, string>();
            foreach (var status in new[] { EventStatus.Cancelled, EventStatus.Archived })
                foreach (var unpublished in new[] { false, true })
                    foreach (var role in new[] { "member", "eventOwner", "questOwner", "invitee", "administrator" })
                        data.Add(status, unpublished, role);
            return data;
        }
    }

    /// <summary>Checks retained unpublished Events are owner-only while published history retains ordinary member access.</summary>
    /// <param name="status">Retained Event lifecycle status.</param>
    /// <param name="unpublished">Whether history contains direct Draft-to-Cancelled rather than publication.</param>
    /// <param name="role">The caller's additional grant, beyond individual membership.</param>
    /// <returns>A task completing after exact Event/child access and nonmutation assertions.</returns>
    [Theory]
    [MemberData(nameof(AccessCases))]
    public async Task RetainedEventPrivacy_RestrictsEveryNonownerGrant_AndPreservesPublishedAccess(
        EventStatus status, bool unpublished, string role)
    {
        var seed = await SetupAsync(status, unpublished, role);
        var current = StubCurrentUser.For(seed.User);
        var access = new ResourceAccess(current);
        await using var db = database.CreateContext();
        var allowed = !unpublished || role == "eventOwner";
        if (allowed)
        {
            var result = await access.RequireEventAsync(db, seed.Event.Id, seed.User.Id);
            Assert.Equal(seed.Event.Id, result.Id);
            Assert.Equal("Private event details", result.Description);
            Assert.Equal(status, result.Status);
            Assert.Equal(seed.Quest.Id, (await access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id)).Id);
        }
        else
        {
            await DeniedAsync(() => access.RequireEventAsync(db, seed.Event.Id, seed.User.Id));
            // The deliberately retained child cannot bypass parent privacy through a public, owner, or invitation grant.
            await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id));
        }
        if (role == "eventOwner")
            Assert.Equal(seed.Event.Id, (await access.RequireEventAsync(db, seed.Event.Id, seed.User.Id, ownerOnly: true)).Id);
        else
            await DeniedAsync(() => access.RequireEventAsync(db, seed.Event.Id, seed.User.Id, ownerOnly: true));
        Assert.Equal(3, current.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var persisted = database.CreateContext();
        Assert.Equal(seed.Event.Version, (await persisted.Events.SingleAsync(x => x.Id == seed.Event.Id)).Version);
        Assert.Equal(unpublished ? 1 : 0, await persisted.EventStatusHistory.CountAsync(x =>
            x.EventId == seed.Event.Id && x.Previous == EventStatus.Draft && x.Next == EventStatus.Cancelled));
        Assert.Equal(MembershipStatus.Active,
            (await persisted.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id)).Status);
    }

    /// <summary>Checks each authorization re-reads the Event marker without requiring a fresh context, identity, or tracked Event.</summary>
    /// <param name="status">The cancelled or archived Event state already tracked by the caller.</param>
    /// <returns>A task completing after same-instance revocation and explicit owner-grant controls.</returns>
    [Theory]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task NewlyPersistedEventMarker_RevokesSameContextAccess_WithoutAffectingOtherEvents(EventStatus status)
    {
        var seed = await SetupAsync(status, false, "questOwner");
        var other = FoundationSeed.NewEvent(seed.Other.Id);
        other.Status = EventStatus.Cancelled;
        await FoundationSeed.PersistAsync(database, other);
        await FoundationSeed.PersistAsync(database, History(other.Id, EventStatus.Draft, EventStatus.Cancelled));
        var access = new ResourceAccess(StubCurrentUser.For(seed.User));
        await using var db = database.CreateContext();
        var tracked = await access.RequireEventAsync(db, seed.Event.Id, seed.User.Id);
        Assert.Equal(seed.Quest.Id, (await access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id)).Id);

        await FoundationSeed.PersistAsync(database, History(seed.Event.Id, EventStatus.Draft, EventStatus.Cancelled));
        await DeniedAsync(() => access.RequireEventAsync(db, seed.Event.Id, seed.User.Id));
        await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id));
        Assert.Same(tracked, db.Events.Local.Single());
        await FoundationSeed.PersistAsync(database, new EventOwner { EventId = seed.Event.Id, UserId = seed.User.Id });
        Assert.Same(tracked, await access.RequireEventAsync(db, seed.Event.Id, seed.User.Id));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Checks Event ownership cannot override account eligibility, verified departure, or removed membership after an allowed read.</summary>
    /// <param name="gate">The prerequisite revoked in an independent SQL context.</param>
    /// <returns>A task completing after exact denial and preservation of ownership and lifecycle history.</returns>
    [Theory]
    [InlineData("membership")]
    [InlineData("eligibility")]
    [InlineData("departure")]
    public async Task RetainedEventOwner_StillRequiresCurrentMembershipAndEligibility(string gate)
    {
        var seed = await SetupAsync(EventStatus.Archived, true, "eventOwner");
        var access = new ResourceAccess(StubCurrentUser.For(seed.User));
        await using var db = database.CreateContext();
        Assert.Equal(seed.Event.Id, (await access.RequireEventAsync(db, seed.Event.Id, seed.User.Id)).Id);
        await using (var change = database.CreateContext())
        {
            if (gate == "membership")
                (await change.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id)).Status = MembershipStatus.Removed;
            else if (gate == "eligibility")
                (await change.Users.SingleAsync(x => x.Id == seed.User.Id)).IsEligible = false;
            else
                (await change.Users.SingleAsync(x => x.Id == seed.User.Id)).DepartureVerifiedUtc = FoundationSeed.Now;
            await change.SaveChangesAsync();
        }
        await DeniedAsync(() => access.RequireEventAsync(db, seed.Event.Id, seed.User.Id),
            gate == "membership" ? ErrorCode.NotFound : ErrorCode.Forbidden);
        Assert.True(await db.EventOwners.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.User.Id));
        Assert.True(await db.EventStatusHistory.AnyAsync(x => x.EventId == seed.Event.Id &&
            x.Previous == EventStatus.Draft && x.Next == EventStatus.Cancelled));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private async Task<FoundationSeed> SetupAsync(EventStatus status, bool unpublished, string role)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await using var db = database.CreateContext();
        (await db.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status = status;
        db.EventMemberships.Add(seed.Membership());
        if (role == "eventOwner")
            db.EventOwners.Add(new EventOwner { EventId = seed.Event.Id, UserId = seed.User.Id });
        if (role == "questOwner")
            db.QuestOwners.Add(new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id });
        if (role == "invitee")
            db.QuestInvitations.Add(seed.Invitation());
        if (role == "administrator")
            db.Administrators.Add(new Administrator { UserId = seed.User.Id });
        if (!unpublished)
            db.EventStatusHistory.Add(History(seed.Event.Id, EventStatus.Draft, EventStatus.Active));
        db.EventStatusHistory.Add(History(seed.Event.Id, unpublished ? EventStatus.Draft : EventStatus.Active, EventStatus.Cancelled));
        if (status == EventStatus.Archived)
            db.EventStatusHistory.Add(History(seed.Event.Id, EventStatus.Cancelled, EventStatus.Archived));
        await db.SaveChangesAsync();
        return seed with { Event = await db.Events.AsNoTracking().SingleAsync(x => x.Id == seed.Event.Id) };
    }

    private static EventStatusHistory History(Guid eventId, EventStatus previous, EventStatus next) => new()
    {
        EventId = eventId,
        Previous = previous,
        Next = next,
        OccurredUtc = FoundationSeed.Now,
        Reason = "Retained lifecycle evidence"
    };

    private static async Task DeniedAsync(Func<Task> operation, ErrorCode code = ErrorCode.NotFound)
    {
        var error = await Assert.ThrowsAsync<DomainException>(operation);
        Assert.Equal(code, error.Code);
        Assert.Equal(code == ErrorCode.NotFound ? "This resource is unavailable." : "Your account is not eligible for Sidequest.",
            error.Message);
        Assert.Null(error.Field);
        Assert.Null(error.InnerException);
    }
}
