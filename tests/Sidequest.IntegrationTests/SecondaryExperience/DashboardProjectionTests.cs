using Sidequest.Application.Experience;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreQuests;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.SecondaryExperience;

/// <summary>Uses actual migrated SQL and the authoritative Quest service to prove home category boundaries.</summary>
/// <param name="database">Fixture owning only a unique GUID-named SidequestTests catalog.</param>
public sealed class DashboardProjectionTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Home statistics and lists classify only authorized effective lifecycle states and exclude joined invitations.</summary>
    /// <returns>Completion after exact active, upcoming, past, top-three, and invitation assertions.</returns>
    [Fact]
    public async Task HomeProjectionClassifiesVisibleQuestsAndInvitations()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var seed = scenario.Seed;
        var upcomingJoined = Quest(seed, "Upcoming joined", 3);
        var followed = Enumerable.Range(1, 4)
            .Select(index => Quest(seed, $"Followed {index}", 3 + index))
            .ToArray();
        var past = Quest(seed, "Past", -4);
        past.EndUtc = FoundationSeed.Now.AddHours(-2);
        past.Status = QuestStatus.Completed;
        var cancelled = Quest(seed, "Cancelled future", 9);
        cancelled.Status = QuestStatus.Cancelled;
        var invitation = Quest(seed, "Invitation", 10);
        invitation.Visibility = QuestVisibility.Private;
        var joinedInvitation = Quest(seed, "Joined invitation", 11);
        joinedInvitation.Visibility = QuestVisibility.Private;
        await FoundationSeed.PersistAsync(database,
            upcomingJoined, past, cancelled, invitation, joinedInvitation);
        await FoundationSeed.PersistAsync(database, followed);
        await FoundationSeed.PersistAsync(database,
            Participation(upcomingJoined, seed.User, ParticipationStatus.Joined),
            Participation(followed[0], seed.User, ParticipationStatus.Following),
            Participation(followed[1], seed.User, ParticipationStatus.Following),
            Participation(followed[2], seed.User, ParticipationStatus.Following),
            Participation(followed[3], seed.User, ParticipationStatus.Following),
            Participation(joinedInvitation, seed.User, ParticipationStatus.Joined),
            Invitation(invitation, seed),
            Invitation(joinedInvitation, seed));
        scenario.Clock.Now = FoundationSeed.Now;

        var result = await new QuestHomeService(scenario.Service(seed.User), scenario.Clock).GetAsync();

        Assert.Equal(2, result.UpcomingJoinedCount);
        Assert.Equal(7, result.UpcomingTotalCount);
        Assert.Equal(seed.Quest.Id, Assert.Single(result.ActiveQuests).Id);
        Assert.Equal(1, result.ActiveCount);
        Assert.Equal(1, result.PastCount);
        Assert.Equal(new[] { upcomingJoined.Id, joinedInvitation.Id }, result.UpcomingJoined.Select(item => item.Id));
        Assert.Equal(followed.Take(3).Select(item => item.Id), result.UpcomingFollowing.Select(item => item.Id));
        Assert.Equal(invitation.Id, Assert.Single(result.Invitations).Id);
        Assert.DoesNotContain(result.UpcomingFollowing, item => item.Id == followed[3].Id);
        Assert.DoesNotContain(result.Invitations, item => item.Id == joinedInvitation.Id);
    }

    private static Quest Quest(FoundationSeed seed, string title, int startHours)
    {
        var quest = FoundationSeed.NewQuest(seed.Event.Id, seed.Other.Id);
        quest.Title = title;
        quest.StartUtc = FoundationSeed.Now.AddHours(startHours);
        quest.EndUtc = quest.StartUtc.AddHours(1);
        return quest;
    }

    private static QuestParticipation Participation(
        Quest quest, UserAccount user, ParticipationStatus status) =>
        new() { QuestId = quest.Id, UserId = user.Id, Status = status };

    private static QuestInvitation Invitation(Quest quest, FoundationSeed seed) =>
        new()
        {
            QuestId = quest.Id,
            UserId = seed.User.Id,
            InvitedById = seed.Other.Id,
            Status = QuestInvitationStatus.Active,
            ChangedUtc = FoundationSeed.Now
        };
}
