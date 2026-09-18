using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Exercises the Quest service authorization surface beyond the shared access-policy unit tests.</summary>
/// <param name="database">Existing real migrated-SQL fixture with independent scenario rows.</param>
public sealed class QuestAuthorizationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Every public Quest command and detail/history query rejects a departed actor, without creator/owner/admin exceptions.</summary>
    /// <returns>Completion after all command surfaces return Forbidden and persisted state is unchanged.</returns>
    [Fact]
    public async Task DepartedActor_EveryServiceEntryPointReauthorizes()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = scenario.Seed.Quest.Id;
        var version = (await service.GetAsync(id)).Summary.Version;
        await using (var db = database.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == scenario.Seed.User.Id)).DepartureVerifiedUtc = scenario.Clock.Now;
            await db.SaveChangesAsync();
        }
        Func<Task>[] calls =
        [
            () => service.ListAsync(QuestListKind.Joined, null, new PageRequest()),
            () => service.ListAsync(QuestListKind.Joined, null, new PageRequest(), new QuestDateFilter()),
            () => service.GetAsync(id),
            () => service.GetAsync(id, true),
            () => service.CreateAsync(scenario.Seed.Event.Id, scenario.Input()),
            () => service.EditAsync(id, version, scenario.Input()),
            () => service.ChangeStatusAsync(id, version, QuestStatus.Cancelled, "Cancellation reason."),
            () => service.DeleteDraftAsync(id, version),
            () => service.ParticipateAsync(id, ParticipationCommand.Leave),
            () => service.InviteAsync(id, scenario.Seed.Other.Id),
            () => service.RevokeInvitationAsync(id, scenario.Seed.Other.Id, "Revocation reason."),
            () => service.RemoveAttendeeAsync(id, scenario.Seed.Other.Id, "Attendee removal reason."),
            () => service.AddOwnerAsync(id, scenario.Seed.Other.Id),
            () => service.RemoveOwnerAsync(id, scenario.Seed.User.Id),
            () => service.HistoryAsync(id),
            () => service.GetOfflineJoinedAsync()
        ];
        foreach (var call in calls)
            Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(call)).Code);
        await using var read = database.CreateContext();
        Assert.Equal(QuestStatus.Active, (await read.Quests.SingleAsync(q => q.Id == id)).Status);
        Assert.False(await read.AuditEntries.AnyAsync(a => a.ResourceId == id));
        Assert.False(await read.OutboxMessages.AnyAsync(o => o.AggregateId == id));
    }

    /// <summary>Event ownership does not grant any ordinary Quest mutation, even when it grants a dedicated moderation path.</summary>
    /// <returns>Completion after ordinary mutation denials and unchanged content/ownership/participation.</returns>
    [Fact]
    public async Task EventOwner_CannotUseAnyOrdinaryQuestManagementCommand()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service(scenario.Seed.Other);
        var id = scenario.Seed.Quest.Id;
        var detail = await service.GetAsync(id);
        Func<Task>[] calls =
        [
            () => service.EditAsync(id, detail.Summary.Version, scenario.Input()),
            () => service.ChangeStatusAsync(id, detail.Summary.Version, QuestStatus.Cancelled, "Cancellation reason."),
            () => service.DeleteDraftAsync(id, detail.Summary.Version),
            () => service.InviteAsync(id, scenario.Seed.Other.Id),
            () => service.RevokeInvitationAsync(id, scenario.Seed.Other.Id, "Revocation reason."),
            () => service.RemoveAttendeeAsync(id, scenario.Seed.User.Id, "Attendee removal reason."),
            () => service.AddOwnerAsync(id, scenario.Seed.Other.Id),
            () => service.RemoveOwnerAsync(id, scenario.Seed.User.Id)
        ];
        foreach (var call in calls)
            Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(call)).Code);
        Assert.False(detail.Summary.IsOwner);
        Assert.True(detail.Summary.CanModerate);
        Assert.Equal(ParticipationStatus.None, detail.Summary.Participation);
        await using var read = database.CreateContext();
        Assert.False(await read.AuditEntries.AnyAsync(a => a.ResourceId == id));
        Assert.Equal(scenario.Seed.User.Id, (await read.QuestOwners.SingleAsync(o => o.QuestId == id)).UserId);
    }

    /// <summary>Membership and invitation are both required for ordinary private reads in every retained lifecycle state.</summary>
    /// <param name="status">Published or retained Quest state.</param>
    /// <returns>Completion after denial before grant, access after grant, and denial after membership loss.</returns>
    [Theory]
    [InlineData(QuestStatus.Active)]
    [InlineData(QuestStatus.Suspended)]
    [InlineData(QuestStatus.Completed)]
    [InlineData(QuestStatus.Cancelled)]
    [InlineData(QuestStatus.Archived)]
    public async Task PrivateRead_RequiresInvitationAndMembershipAcrossRetainedStates(QuestStatus status)
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var viewer = scenario.Service(scenario.Seed.Other);
        await using (var db = database.CreateContext())
        {
            (await db.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id)).Status = status;
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => viewer.GetAsync(scenario.Seed.Quest.Id))).Code);
        await FoundationSeed.PersistAsync(database, new QuestInvitation
        {
            QuestId = scenario.Seed.Quest.Id,
            UserId = scenario.Seed.Other.Id,
            InvitedById = scenario.Seed.User.Id,
            ChangedUtc = scenario.Clock.Now,
            Status = QuestInvitationStatus.Active
        });
        var detail = await viewer.GetAsync(scenario.Seed.Quest.Id);
        Assert.Equal(status, detail.Summary.Status);
        Assert.Null(detail.Followers);
        Assert.Null(detail.Invitees);
        await using (var db = database.CreateContext())
        {
            (await db.EventMemberships.SingleAsync(m => m.EventId == scenario.Seed.Event.Id && m.UserId == scenario.Seed.Other.Id)).Status = MembershipStatus.Removed;
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => viewer.GetAsync(scenario.Seed.Quest.Id))).Code);
        Assert.Empty((await viewer.ListAsync(QuestListKind.History, null, new())).Items);
    }

    /// <summary>Ordinary viewers receive attendee display names and counts, but not attendee emails, follower or invitation rosters, or full moderation history.</summary>
    /// <returns>Completion after independent owner/ordinary roster and history assertions.</returns>
    [Fact]
    public async Task OrdinaryRoster_ExposesAttendeeNamesOnly_AndHistoryIsRestricted()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var owner = scenario.Service();
        var viewer = scenario.Service(scenario.Seed.Other);
        await owner.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Follow);
        await viewer.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
        var detail = await viewer.GetAsync(scenario.Seed.Quest.Id);
        Assert.Equal(scenario.Seed.Other.Id, Assert.Single(detail.Attendees!).Id);
        Assert.Equal(scenario.Seed.Other.DisplayName, Assert.Single(detail.Attendees!).DisplayName);
        Assert.Equal(1, detail.Summary.AttendeeCount);
        Assert.Equal(1, detail.Summary.FollowerCount);
        Assert.Null(detail.Followers);
        Assert.Null(detail.Invitees);
        Assert.Equal(new[] { "DisplayName", "Id" }, typeof(QuestRosterPersonSummary).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain(scenario.Seed.Other.Email, System.Text.Json.JsonSerializer.Serialize(detail.Attendees));
        var ownerDetail = await owner.GetAsync(scenario.Seed.Quest.Id);
        Assert.Equal(scenario.Seed.User.Id, Assert.Single(ownerDetail.Followers!).Id);
        Assert.Equal(scenario.Seed.User.DisplayName, Assert.Single(ownerDetail.Followers!).DisplayName);
        Assert.DoesNotContain(scenario.Seed.User.Email, System.Text.Json.JsonSerializer.Serialize(ownerDetail.Followers));
        var ordinaryHistory = Assert.Single(await viewer.HistoryAsync(scenario.Seed.Quest.Id));
        Assert.Equal("Active", ordinaryHistory.Action);
        Assert.Null(ordinaryHistory.Actor);
        var history = await owner.HistoryAsync(scenario.Seed.Quest.Id);
        Assert.Equal(2, history.Count);
        Assert.Contains(history, item => item.Action.Contains($"{scenario.Seed.Other.Email} ({scenario.Seed.Other.DisplayName})", StringComparison.Ordinal));
        Assert.Contains(history, item => item.Actor == $"{scenario.Seed.User.Email} ({scenario.Seed.User.DisplayName})");
        Assert.All(history, item =>
        {
            Assert.DoesNotContain(scenario.Seed.User.Id.ToString("N"), item.Action);
            Assert.DoesNotContain(scenario.Seed.Other.Id.ToString("N"), item.Action);
        });
    }

    /// <summary>Even an owner receives names-only attendee, follower, and invitation rosters; moderation continues withholding all three.</summary>
    /// <returns>Completion after actual invitation and participation commands prove roster identities, email exclusion, and moderation redaction.</returns>
    [Fact]
    public async Task PrivateRosters_RemainNamesOnlyForOwners_AndWithheldFromModeration()
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var owner = scenario.Service();
        var viewer = scenario.Service(scenario.Seed.Other);
        await owner.InviteAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id);
        await owner.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Follow);
        await viewer.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);

        var detail = await owner.GetAsync(scenario.Seed.Quest.Id);
        Assert.Equal(new QuestRosterPersonSummary(scenario.Seed.Other.Id, scenario.Seed.Other.DisplayName),
            Assert.Single(detail.Attendees!));
        Assert.Equal(new QuestRosterPersonSummary(scenario.Seed.User.Id, scenario.Seed.User.DisplayName),
            Assert.Single(detail.Followers!));
        Assert.Equal(new QuestRosterPersonSummary(scenario.Seed.Other.Id, scenario.Seed.Other.DisplayName),
            Assert.Single(detail.Invitees!));
        foreach (var roster in new[] { detail.Attendees, detail.Followers, detail.Invitees })
        {
            var serialized = System.Text.Json.JsonSerializer.Serialize(roster);
            Assert.DoesNotContain(scenario.Seed.User.Email, serialized);
            Assert.DoesNotContain(scenario.Seed.Other.Email, serialized);
            Assert.DoesNotContain("\"Email\"", serialized);
        }

        var moderation = await viewer.GetAsync(scenario.Seed.Quest.Id, moderation: true);
        Assert.Null(moderation.Attendees);
        Assert.Null(moderation.Followers);
        Assert.Null(moderation.Invitees);
    }

    /// <summary>Historical actions whose target no longer resolves use an explicit safe label rather than displaying the internal identifier.</summary>
    /// <returns>Completion after owner projection redacts the unresolved target and ordinary access still withholds detailed history.</returns>
    [Fact]
    public async Task History_UnresolvedPersonUsesSafeLabelWithoutExposingIdentifier()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var unresolved = Guid.NewGuid();
        await FoundationSeed.PersistAsync(database, new AuditEntry
        {
            ResourceKind = ResourceKind.Quest, ResourceId = scenario.Seed.Quest.Id,
            ActorId = scenario.Seed.User.Id, Action = $"OwnerRemoved:{unresolved:N}",
            Reason = "", OccurredUtc = scenario.Clock.Now, CorrelationId = Guid.NewGuid().ToString("N")
        });
        var history = Assert.Single(await scenario.Service().HistoryAsync(scenario.Seed.Quest.Id));
        Assert.Equal("OwnerRemoved:Unavailable person", history.Action);
        Assert.Equal($"{scenario.Seed.User.Email} ({scenario.Seed.User.DisplayName})", history.Actor);
        Assert.DoesNotContain(unresolved.ToString("N"), history.Action);
        var ordinary = Assert.Single(await scenario.Service(scenario.Seed.Other).HistoryAsync(scenario.Seed.Quest.Id));
        Assert.Equal("Active", ordinary.Action);
        Assert.Null(ordinary.Actor);
    }

    /// <summary>Direct Quest lists and offline snapshots preserve never-published Event privacy through archive without hiding genuine published-history controls.</summary>
    /// <param name="parentStatus">Cancelled or Archived parent state retaining its original publication provenance.</param>
    /// <param name="published">Whether the parent's history records publication before cancellation rather than direct Draft cancellation.</param>
    /// <param name="eventOwner">Whether the eligible active member also owns the parent, independently of owning both child Quests.</param>
    /// <returns>Completion after exact list IDs/totals, both list overloads, offline IDs and owner membership-loss assertions.</returns>
    [Theory]
    [InlineData(EventStatus.Cancelled, false, false)]
    [InlineData(EventStatus.Cancelled, false, true)]
    [InlineData(EventStatus.Cancelled, true, false)]
    [InlineData(EventStatus.Cancelled, true, true)]
    [InlineData(EventStatus.Archived, false, false)]
    [InlineData(EventStatus.Archived, false, true)]
    [InlineData(EventStatus.Archived, true, false)]
    [InlineData(EventStatus.Archived, true, true)]
    public async Task ParentPublicationHistory_ProtectsDirectListsAndOfflineSnapshots(
        EventStatus parentStatus, bool published, bool eventOwner)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var historyQuest = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        historyQuest.Status = QuestStatus.Cancelled;
        await FoundationSeed.PersistAsync(database, historyQuest,
            new QuestOwner { QuestId = historyQuest.Id, UserId = scenario.Seed.User.Id });
        await using (var db = database.CreateContext())
        {
            (await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status = parentStatus;
            if (eventOwner)
                db.EventOwners.Add(new EventOwner { EventId = scenario.Seed.Event.Id, UserId = scenario.Seed.User.Id });
            if (published)
                db.EventStatusHistory.Add(new EventStatusHistory
                {
                    EventId = scenario.Seed.Event.Id,
                    Previous = EventStatus.Draft,
                    Next = EventStatus.Active,
                    OccurredUtc = scenario.Clock.Now.AddHours(-1),
                    ActorId = scenario.Seed.Other.Id,
                    Reason = "The Event was published."
                });
            db.EventStatusHistory.Add(new EventStatusHistory
            {
                EventId = scenario.Seed.Event.Id,
                Previous = published ? EventStatus.Active : EventStatus.Draft,
                Next = EventStatus.Cancelled,
                OccurredUtc = scenario.Clock.Now,
                ActorId = scenario.Seed.Other.Id,
                Reason = "The Event was cancelled."
            });
            if (parentStatus == EventStatus.Archived)
                db.EventStatusHistory.Add(new EventStatusHistory
                {
                    EventId = scenario.Seed.Event.Id,
                    Previous = EventStatus.Cancelled,
                    Next = EventStatus.Archived,
                    OccurredUtc = scenario.Clock.Now,
                    ActorId = scenario.Seed.Other.Id,
                    Reason = "The Event was archived."
                });
            foreach (var id in new[] { scenario.Seed.Quest.Id, historyQuest.Id })
                db.Participations.Add(new QuestParticipation
                {
                    QuestId = id,
                    UserId = scenario.Seed.User.Id,
                    Status = ParticipationStatus.Joined,
                    ChangedUtc = scenario.Clock.Now
                });
            await db.SaveChangesAsync();
        }

        var service = scenario.Service();
        var visible = published || eventOwner;
        foreach (var kind in new[] { QuestListKind.Joined, QuestListKind.Organizing, QuestListKind.History })
        {
            var expected = kind == QuestListKind.History ? historyQuest.Id : scenario.Seed.Quest.Id;
            foreach (var dated in new[] { false, true })
            {
                var page = dated
                    ? await service.ListAsync(kind, null, new(1, 1),
                        new QuestDateFilter(FoundationSeed.Now.AddTicks(-1), FoundationSeed.Now.AddTicks(1)))
                    : await service.ListAsync(kind, null, new(1, 1));
                Assert.Equal(visible ? 1 : 0, page.TotalCount);
                Assert.Equal(visible ? [expected] : Array.Empty<Guid>(), page.Items.Select(q => q.Id).ToArray());
            }
        }
        var offline = await service.GetOfflineJoinedAsync();
        var expectedOffline = visible ? new[] { scenario.Seed.Quest.Id, historyQuest.Id } : [];
        Assert.Equal(expectedOffline.OrderBy(id => id), offline.Select(q => q.Id).OrderBy(id => id));
        await using (var db = database.CreateContext())
        {
            (await db.EventMemberships.SingleAsync(m => m.EventId == scenario.Seed.Event.Id &&
                m.UserId == scenario.Seed.User.Id)).Status = MembershipStatus.Removed;
            await db.SaveChangesAsync();
        }
        var denied = await service.ListAsync(QuestListKind.History, null, new());
        Assert.Equal(0, denied.TotalCount);
        Assert.Empty(denied.Items);
        Assert.Empty(await service.GetOfflineJoinedAsync());
    }

    /// <summary>List paging is deterministic, private resources never inflate discovery totals, and invalid paging is rejected.</summary>
    /// <returns>Completion after exact page IDs, matching totals and hidden private-resource assertions.</returns>
    [Fact]
    public async Task DiscoveryPaging_UsesStableIdTieBreak_AndExcludesPrivateHints()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var first = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        var second = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        var hidden = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        hidden.Visibility = QuestVisibility.Private;
        await FoundationSeed.PersistAsync(database, first, second, hidden);
        var viewer = scenario.Service(scenario.Seed.Other);
        var one = await viewer.ListAsync(QuestListKind.Discover, scenario.Seed.Event.Id, new(1, 2));
        var two = await viewer.ListAsync(QuestListKind.Discover, scenario.Seed.Event.Id, new(2, 2));
        Assert.Equal(3, one.TotalCount);
        Assert.Equal(3, two.TotalCount);
        Assert.Equal(2, one.Items.Count);
        Assert.Single(two.Items);
        await using var db = database.CreateContext();
        var expected = await db.Quests.Where(q => q.EventId == scenario.Seed.Event.Id && q.Visibility == QuestVisibility.Public)
            .OrderBy(q => q.StartUtc).ThenBy(q => q.Id).Select(q => q.Id).ToArrayAsync();
        Assert.Equal(expected, one.Items.Concat(two.Items).Select(q => q.Id).ToArray());
        Assert.DoesNotContain(one.Items.Concat(two.Items), q => q.Id == hidden.Id);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            viewer.ListAsync(QuestListKind.Discover, null, new(0)))).Code);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            viewer.ListAsync((QuestListKind)999, null, new()))).Code);
    }
}
