using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreBoundaries;

/// <summary>Checks retained unpublished cancellation privacy through shared SQL-backed authorization, not feature-service implementations.</summary>
/// <param name="database">The existing migrated fixture used for unique independent permission scenarios.</param>
public sealed class CancelledUnpublishedQuestAccessTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Gets literal role/mode outcomes crossed with both retained lifecycle states, visibilities and publication histories.</summary>
    public static TheoryData<QuestStatus, QuestVisibility, string, bool, bool, bool, bool> AccessCases
    {
        get
        {
            var data = new TheoryData<QuestStatus, QuestVisibility, string, bool, bool, bool, bool>();
            (string Role, bool OwnerOnly, bool Moderation, bool Public, bool Private, bool Unpublished)[] policies =
            [
                ("member", false, false, true, false, false),
                ("member", false, true, false, false, false),
                ("invitee", false, false, true, true, false),
                ("invitee", false, true, false, false, false),
                ("admin", false, false, true, false, false),
                ("admin", false, true, false, false, false),
                ("eventOwner", false, false, true, false, false),
                ("eventOwner", false, true, true, true, false),
                ("questOwner", false, false, true, true, true),
                ("questOwner", false, true, true, true, false),
                ("questOwner", true, false, true, true, true),
                ("questOwner", true, true, true, true, false)
            ];
            foreach (var status in new[] { QuestStatus.Cancelled, QuestStatus.Archived })
                foreach (var visibility in new[] { QuestVisibility.Public, QuestVisibility.Private })
                    foreach (var unpublished in new[] { false, true })
                        foreach (var policy in policies)
                            data.Add(status, visibility, policy.Role, policy.OwnerOnly, policy.Moderation, unpublished,
                                unpublished ? policy.Unpublished : visibility == QuestVisibility.Public ? policy.Public : policy.Private);
            return data;
        }
    }

    /// <summary>Checks unpublished cancellations remain Quest-owner-only and never moderation-readable, with genuinely published controls.</summary>
    /// <param name="status">Cancelled or Archived retained Quest state.</param>
    /// <param name="visibility">Public or Private ordinary visibility.</param>
    /// <param name="role">The individual membership/invitation/admin/Event-owner/Quest-owner grant partition.</param>
    /// <param name="ownerOnly">Whether the operation explicitly requires Quest ownership.</param>
    /// <param name="moderation">Whether the separate moderation route is requested.</param>
    /// <param name="unpublished">Whether history records direct Draft-to-Cancelled rather than publication before cancellation.</param>
    /// <param name="allowed">The independently specified access result.</param>
    /// <returns>A task completing after exact content or safe denial and durable history/grant nonmutation assertions.</returns>
    [Theory]
    [MemberData(nameof(AccessCases))]
    public async Task CancelledAndArchived_HistoryAndRoleMatrix_PreservesPrivacy(QuestStatus status,
        QuestVisibility visibility, string role, bool ownerOnly, bool moderation, bool unpublished, bool allowed)
    {
        var seed = await SetupAsync(status, visibility, role, unpublished);
        var fake = StubCurrentUser.For(seed.User);
        var access = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        if (allowed)
        {
            var result = await access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, ownerOnly, moderation);
            Assert.Equal(seed.Quest.Id, result.Id);
            Assert.Equal("Sensitive Quest sentinel", result.Title);
            Assert.Equal("Private quest details", result.Description);
            Assert.Equal(status, result.Status);
            Assert.Equal(visibility, result.Visibility);
        }
        else
            await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, ownerOnly, moderation));
        Assert.Equal(1, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await AssertRetainedAsync(seed, status, role, unpublished);
    }

    /// <summary>Checks active membership, eligibility/departure and non-Draft parent gates still precede retained Quest-owner access.</summary>
    /// <param name="gate">The independently invalid parent/account prerequisite.</param>
    /// <returns>A task completing after exact denial and proof that the Quest ownership/history grants remain stored.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("removed")]
    [InlineData("request")]
    [InlineData("invitation")]
    [InlineData("draftParent")]
    [InlineData("ineligible")]
    [InlineData("departed")]
    public async Task UnpublishedQuestOwner_ParentAndIdentityGuardsStillApply(string gate)
    {
        var seed = await SetupAsync(QuestStatus.Archived, QuestVisibility.Private, "questOwner", true);
        await using (var change = database.CreateContext())
        {
            if (gate is "missing" or "request" or "invitation")
                change.EventMemberships.Remove(await change.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id));
            if (gate == "removed")
                (await change.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id)).Status = MembershipStatus.Removed;
            if (gate == "request")
                change.MembershipRequests.Add(new EventMembershipRequest
                {
                    EventId = seed.Event.Id, UserId = seed.User.Id, Status = MembershipRequestStatus.Pending,
                    Reason = "Pending is not membership", CreatedUtc = FoundationSeed.Now
                });
            if (gate == "invitation")
                change.EventInvitations.Add(new EventInvitation
                {
                    EventId = seed.Event.Id, UserId = seed.User.Id, InvitedById = seed.Other.Id,
                    Status = EventInvitationStatus.Pending, CreatedUtc = FoundationSeed.Now,
                    ExpiresUtc = FoundationSeed.Now.AddDays(1)
                });
            if (gate == "draftParent")
                (await change.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status = EventStatus.Draft;
            if (gate == "ineligible")
                (await change.Users.SingleAsync(x => x.Id == seed.User.Id)).IsEligible = false;
            if (gate == "departed")
                (await change.Users.SingleAsync(x => x.Id == seed.User.Id)).DepartureVerifiedUtc = FoundationSeed.Now;
            await change.SaveChangesAsync();
        }
        await using var db = database.CreateContext();
        var access = new ResourceAccess(StubCurrentUser.For(seed.User));
        foreach (var ownerOnly in new[] { false, true })
            await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, ownerOnly),
                gate is "ineligible" or "departed");
        Assert.False(db.ChangeTracker.HasChanges());
        await AssertRetainedAsync(seed, QuestStatus.Archived, "questOwner", true);
    }

    /// <summary>Checks a newly committed exact marker revokes ordinary/moderation exposure without refreshing the tracked Quest, service or identity.</summary>
    /// <param name="status">The retained cancelled or archived lifecycle state.</param>
    /// <returns>A task completing after positive controls, cross-context marker insertion, same-context denials and retained owner ordinary access.</returns>
    [Theory]
    [InlineData(QuestStatus.Cancelled)]
    [InlineData(QuestStatus.Archived)]
    public async Task NewDraftCancellationMarker_SameTrackedContext_RechecksPrivacy(QuestStatus status)
    {
        var seed = await SetupAsync(status, QuestVisibility.Public, "eventOwner", false);
        var fake = StubCurrentUser.For(seed.User);
        var access = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        var tracked = await access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id);
        Assert.Same(tracked, await access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, moderation: true));
        await FoundationSeed.PersistAsync(database, History(seed, QuestStatus.Draft, QuestStatus.Cancelled));
        await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id));
        await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, moderation: true));
        Assert.Same(tracked, db.Quests.Local.Single());
        Assert.Equal(4, fake.Calls);
        await FoundationSeed.PersistAsync(database, new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id });
        Assert.Same(tracked, await access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, ownerOnly: true));
        await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, true, true));
        Assert.Equal(6, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        Assert.Equal(1, await read.QuestStatusHistory.CountAsync(x => x.QuestId == seed.Quest.Id &&
            x.Previous == QuestStatus.Draft && x.Next == QuestStatus.Cancelled));
        Assert.Equal(status, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
    }

    /// <summary>Checks verified departure revokes an unpublished Quest owner's previously allowed read without replacing identity or tracked context.</summary>
    /// <returns>A task completing after before/after access assertions and retained eligibility/ownership/history observations.</returns>
    [Fact]
    public async Task UnpublishedOwner_DepartureAfterAllowedRead_DeniesSameInstance()
    {
        var seed = await SetupAsync(QuestStatus.Archived, QuestVisibility.Private, "questOwner", true);
        var fake = StubCurrentUser.For(seed.User);
        var access = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        var tracked = await access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, ownerOnly: true);
        Assert.Equal(seed.Quest.Id, tracked.Id);
        await using (var update = database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == seed.User.Id)).DepartureVerifiedUtc = FoundationSeed.Now;
            Assert.Equal(1, await update.SaveChangesAsync());
        }
        await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id), forbidden: true);
        await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, true), forbidden: true);
        Assert.Same(tracked, db.Quests.Local.Single());
        Assert.Equal(3, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        var departed = await read.Users.SingleAsync(x => x.Id == seed.User.Id);
        Assert.True(departed.IsEligible);
        Assert.Equal(FoundationSeed.Now, departed.DepartureVerifiedUtc);
        await AssertRetainedAsync(seed, QuestStatus.Archived, "questOwner", true);
    }

    /// <summary>Checks a different Quest's direct cancellation marker and nonmatching transitions do not hide a published cancelled Quest.</summary>
    /// <returns>A task completing after exact target content and separate marker-count assertions.</returns>
    [Fact]
    public async Task OtherQuestMarkerAndNonmatchingTransitions_DoNotDenyPublishedTarget()
    {
        var seed = await SetupAsync(QuestStatus.Cancelled, QuestVisibility.Private, "invitee", false);
        var otherQuest = FoundationSeed.NewQuest(seed.Event.Id, seed.Other.Id);
        await FoundationSeed.PersistAsync(database, otherQuest);
        var marker = History(seed, QuestStatus.Draft, QuestStatus.Cancelled);
        marker.QuestId = otherQuest.Id;
        await FoundationSeed.PersistAsync(database, marker, History(seed, QuestStatus.Draft, QuestStatus.Archived));
        await using var db = database.CreateContext();
        var result = await new ResourceAccess(StubCurrentUser.For(seed.User))
            .RequireQuestAsync(db, seed.Quest.Id, seed.User.Id);
        Assert.Equal(seed.Quest.Id, result.Id);
        Assert.Equal("Sensitive Quest sentinel", result.Title);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(1, await db.QuestStatusHistory.CountAsync(x => x.QuestId == otherQuest.Id));
        Assert.Equal(0, await db.QuestStatusHistory.CountAsync(x => x.QuestId == seed.Quest.Id &&
            x.Previous == QuestStatus.Draft && x.Next == QuestStatus.Cancelled));
    }

    private async Task<FoundationSeed> SetupAsync(QuestStatus status, QuestVisibility visibility, string role, bool unpublished)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await using var db = database.CreateContext();
        var quest = await db.Quests.SingleAsync(x => x.Id == seed.Quest.Id);
        quest.Status = status;
        quest.Visibility = visibility;
        db.EventMemberships.Add(seed.Membership());
        if (role == "invitee")
            db.QuestInvitations.Add(seed.Invitation());
        if (role is "eventOwner" or "questOwner")
            db.EventOwners.Add(new EventOwner { EventId = seed.Event.Id, UserId = seed.User.Id });
        if (role == "questOwner")
        {
            db.QuestOwners.Add(new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id });
            db.QuestInvitations.Add(seed.Invitation());
            db.Administrators.Add(new Administrator { UserId = seed.User.Id });
        }
        if (role == "admin")
            db.Administrators.Add(new Administrator { UserId = seed.User.Id });
        if (unpublished)
            db.QuestStatusHistory.Add(History(seed, QuestStatus.Draft, QuestStatus.Cancelled));
        else
            db.QuestStatusHistory.AddRange(History(seed, QuestStatus.Draft, QuestStatus.Active),
                History(seed, QuestStatus.Active, QuestStatus.Cancelled));
        if (status == QuestStatus.Archived)
            db.QuestStatusHistory.Add(History(seed, QuestStatus.Cancelled, QuestStatus.Archived));
        await db.SaveChangesAsync();
        return seed;
    }

    private static QuestStatusHistory History(FoundationSeed seed, QuestStatus previous, QuestStatus next) => new()
    {
        QuestId = seed.Quest.Id, Previous = previous, Next = next, ActorId = seed.Other.Id,
        Reason = "Core boundary retained provenance", OccurredUtc = FoundationSeed.Now
    };

    private static async Task DeniedAsync(Func<Task<Quest>> action, bool forbidden = false)
    {
        var error = await Assert.ThrowsAsync<DomainException>(action);
        Assert.Equal(forbidden ? ErrorCode.Forbidden : ErrorCode.NotFound, error.Code);
        Assert.Equal(forbidden ? "Your account is not eligible for Sidequest." : "This resource is unavailable.", error.Message);
        Assert.Null(error.Field);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("Sensitive Quest sentinel", error.ToString());
        Assert.DoesNotContain("Private quest details", error.ToString());
    }

    private async Task AssertRetainedAsync(FoundationSeed seed, QuestStatus status, string role, bool unpublished)
    {
        await using var read = database.CreateContext();
        var quest = await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id);
        Assert.Equal(status, quest.Status);
        Assert.Equal("Sensitive Quest sentinel", quest.Title);
        Assert.Equal(unpublished ? 1 : 0, await read.QuestStatusHistory.CountAsync(x => x.QuestId == seed.Quest.Id &&
            x.Previous == QuestStatus.Draft && x.Next == QuestStatus.Cancelled));
        Assert.Equal(role == "questOwner" ? 1 : 0, await read.QuestOwners.CountAsync(x => x.QuestId == seed.Quest.Id));
        Assert.Equal(role is "questOwner" or "invitee" ? 1 : 0,
            await read.QuestInvitations.CountAsync(x => x.QuestId == seed.Quest.Id));
        Assert.False(await read.Participations.AnyAsync(x => x.QuestId == seed.Quest.Id));
    }
}
