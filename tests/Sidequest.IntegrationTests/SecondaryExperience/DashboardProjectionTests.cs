using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Experience;
using Sidequest.Application.Quests;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreQuests;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.SecondaryExperience;

/// <summary>Uses actual migrated SQL and the real Quest/access services to prove owner statistics and category boundaries.</summary>
/// <param name="database">Fixture owning only a unique GUID-named SidequestTests catalog.</param>
public sealed class DashboardProjectionTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Private owner totals count only active invitations to eligible active members, without exposing private totals to an ordinary invited viewer.</summary>
    /// <returns>Completion after exact owner/viewer count-map assertions and membership revocation.</returns>
    [Fact]
    public async Task PrivateInvitationCountsRequireOwnerAndEligibleCurrentMembership()
    {
        var scenario = await QuestScenario.CreateAsync(database, privateQuest: true);
        var seed = scenario.Seed;
        var removed = FoundationSeed.NewUser();
        var departed = FoundationSeed.NewUser();
        departed.DepartureVerifiedUtc = FoundationSeed.Now;
        var revoked = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, removed, departed, revoked);
        await FoundationSeed.PersistAsync(database,
            seed.Membership(removed.Id, MembershipStatus.Removed), seed.Membership(departed.Id), seed.Membership(revoked.Id),
            Invitation(seed.Quest.Id, seed.Other.Id, seed.User.Id),
            Invitation(seed.Quest.Id, removed.Id, seed.User.Id),
            Invitation(seed.Quest.Id, departed.Id, seed.User.Id),
            Invitation(seed.Quest.Id, revoked.Id, seed.User.Id, QuestInvitationStatus.Revoked));
        var owner = Service(scenario, seed.User);
        var own = await owner.ListAsync(QuestListKind.Organizing, seed.Event.Id, new(), new());
        Assert.Equal(1, own.InvitedCounts[seed.Quest.Id]);
        Assert.Equal(seed.Quest.Id, Assert.Single(own.Quests.Items).Id);
        var viewer = await Service(scenario, seed.Other).ListAsync(QuestListKind.Invited, seed.Event.Id, new(), new());
        Assert.Equal(seed.Quest.Id, Assert.Single(viewer.Quests.Items).Id);
        Assert.Empty(viewer.InvitedCounts);
        await using (var db = database.CreateContext())
        {
            var member = await db.EventMemberships.SingleAsync(m => m.EventId == seed.Event.Id && m.UserId == seed.Other.Id);
            member.Status = MembershipStatus.Removed;
            await db.SaveChangesAsync();
        }
        var refreshed = await owner.ListAsync(QuestListKind.Organizing, seed.Event.Id, new(), new());
        Assert.Equal(0, refreshed.InvitedCounts[seed.Quest.Id]);
    }

    /// <summary>Joined and Following are exclusive, Organizing may overlap, and the real offline service returns only joined basics.</summary>
    /// <returns>Completion after exact category IDs, minimal records and full replacement following leave.</returns>
    [Fact]
    public async Task JoinedFollowingOrganizingAndOfflineProjectionUseActualExclusiveParticipation()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var seed = scenario.Seed;
        var followed = FoundationSeed.NewQuest(seed.Event.Id, seed.User.Id);
        var invited = FoundationSeed.NewQuest(seed.Event.Id, seed.User.Id);
        invited.Visibility = QuestVisibility.Private;
        var ownedOnly = FoundationSeed.NewQuest(seed.Event.Id, seed.User.Id);
        await FoundationSeed.PersistAsync(database, followed, invited, ownedOnly);
        await FoundationSeed.PersistAsync(database,
            new QuestParticipation { QuestId = seed.Quest.Id, UserId = seed.User.Id, Status = ParticipationStatus.Joined },
            new QuestParticipation { QuestId = followed.Id, UserId = seed.User.Id, Status = ParticipationStatus.Following },
            new QuestOwner { QuestId = ownedOnly.Id, UserId = seed.User.Id },
            Invitation(invited.Id, seed.User.Id, seed.Other.Id));
        var service = Service(scenario, seed.User);
        var joined = await service.ListAsync(QuestListKind.Joined, seed.Event.Id, new(), new());
        Assert.Equal(seed.Quest.Id, Assert.Single(joined.Quests.Items).Id);
        var following = await service.ListAsync(QuestListKind.Following, seed.Event.Id, new(), new());
        Assert.Equal(followed.Id, Assert.Single(following.Quests.Items).Id);
        var organizing = await service.ListAsync(QuestListKind.Organizing, seed.Event.Id, new(), new());
        Assert.Equal(2, organizing.Quests.TotalCount);
        Assert.Contains(organizing.Quests.Items, q => q.Id == seed.Quest.Id);
        Assert.Contains(organizing.Quests.Items, q => q.Id == ownedOnly.Id);
        var snapshot = Assert.Single(await scenario.Service().GetOfflineJoinedAsync());
        Assert.Equal(seed.Quest.Id, snapshot.Id);
        Assert.Equal(seed.Quest.Location, snapshot.Location);
        Assert.Equal(seed.Event.TimeZoneId, snapshot.TimeZoneId);
        await scenario.Service().ParticipateAsync(seed.Quest.Id, ParticipationCommand.Leave);
        Assert.Empty(await scenario.Service().GetOfflineJoinedAsync());
    }

    /// <summary>Owner projection preserves the existing service's pre-page filtering, default size, ID tie-breaking and cancellation.</summary>
    /// <returns>Completion after exact bounded-page and cancelled-operation assertions.</returns>
    [Fact]
    public async Task PagingAndDateBoundsRemainInAuthoritativeQuery()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var second = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        second.StartUtc = scenario.Seed.Quest.StartUtc.AddMinutes(1);
        await FoundationSeed.PersistAsync(database, second);
        var service = Service(scenario, scenario.Seed.User);
        var page = await service.ListAsync(QuestListKind.Discover, scenario.Seed.Event.Id, new(1, 1),
            new(second.StartUtc, second.StartUtc.AddMinutes(1)));
        Assert.Equal(second.Id, Assert.Single(page.Quests.Items).Id);
        Assert.Equal(1, page.Quests.TotalCount);
        Assert.Empty(page.InvitedCounts);
        var defaults = await service.ListAsync(QuestListKind.Discover, scenario.Seed.Event.Id, new(), new());
        Assert.Equal(25, defaults.Quests.PageSize);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(
            QuestListKind.Discover, null, new(), new(), cancellation.Token));
    }

    /// <summary>Named invitation alone never enters the offline projection, and actual online revocation removes a previously joined private Quest.</summary>
    /// <returns>Completion after invitation-only, joined and revoked full-projection assertions.</returns>
    [Fact]
    public async Task PrivateInvitationRevocationRemovesJoinedBasicsOnNextAuthorizedQuery()
    {
        var scenario = await QuestScenario.CreateAsync(database, privateQuest: true);
        var owner = scenario.Service();
        var member = scenario.Service(scenario.Seed.Other);
        await owner.InviteAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id);
        Assert.Empty(await member.GetOfflineJoinedAsync());
        await member.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
        Assert.Equal(scenario.Seed.Quest.Id, Assert.Single(await member.GetOfflineJoinedAsync()).Id);
        await owner.RevokeInvitationAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id, "Synthetic access revoked.");
        Assert.Empty(await member.GetOfflineJoinedAsync());
        var invited = await Service(scenario, scenario.Seed.Other).ListAsync(QuestListKind.Invited, scenario.Seed.Event.Id, new(), new());
        Assert.Empty(invited.Quests.Items);
        Assert.Equal(0, invited.Quests.TotalCount);
        Assert.Empty(invited.InvitedCounts);
    }

    /// <summary>Revocation between the authorized page query and the statistics recheck fails closed instead of rendering stale owner-only private content.</summary>
    /// <returns>Completion after real SQL ownership removal and a non-disclosing unavailable outcome.</returns>
    [Fact]
    public async Task RevocationBetweenPageAndStatisticsRejectsStalePrivateCards()
    {
        var scenario = await QuestScenario.CreateAsync(database, privateQuest: true);
        await FoundationSeed.PersistAsync(database,
            new QuestOwner { QuestId = scenario.Seed.Quest.Id, UserId = scenario.Seed.Other.Id });
        var captured = await scenario.Service().ListAsync(QuestListKind.Organizing, scenario.Seed.Event.Id, new());
        Assert.Single(captured.Items);
        await scenario.Service(scenario.Seed.Other).RemoveOwnerAsync(scenario.Seed.Quest.Id, scenario.Seed.User.Id);
        var queries = DispatchProxy.Create<IQuestService, CapturedQuestPageProxy>();
        ((CapturedQuestPageProxy)(object)queries).CapturedPage = captured;
        var service = new DashboardService(queries, new QuestTestFactory(database),
            new ResourceAccess(StubCurrentUser.For(scenario.Seed.User)));
        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            service.ListAsync(QuestListKind.Organizing, scenario.Seed.Event.Id, new(), new()));
        Assert.Equal(ErrorCode.NotFound, failure.Code);
        Assert.DoesNotContain(scenario.Seed.Quest.Title, failure.Message);
    }

    private DashboardService Service(QuestScenario scenario, UserAccount actor) =>
        new(scenario.Service(actor), new QuestTestFactory(database), new ResourceAccess(StubCurrentUser.For(actor)));
    private static QuestInvitation Invitation(Guid quest, Guid user, Guid owner,
        QuestInvitationStatus status = QuestInvitationStatus.Active) =>
        new() { QuestId = quest, UserId = user, InvitedById = owner, Status = status, ChangedUtc = FoundationSeed.Now };

    /// <summary>Moderation is never a dashboard count path, and invalid discriminators fail before a content query.</summary>
    /// <param name="kind">A private moderation or undefined view discriminator.</param>
    /// <returns>Completion after explicit validation assertions.</returns>
    [Theory]
    [InlineData(QuestListKind.Moderation)]
    [InlineData((QuestListKind)100)]
    public async Task DashboardRejectsModerationAndUnknownView(QuestListKind kind)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            Service(scenario, scenario.Seed.User).ListAsync(kind, null, new(), new()));
        Assert.Equal(ErrorCode.Validation, failure.Code);
        Assert.Equal("Kind", failure.Field);
    }
}
