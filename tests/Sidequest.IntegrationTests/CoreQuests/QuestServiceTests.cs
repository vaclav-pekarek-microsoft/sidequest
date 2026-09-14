using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Verifies Quest authorization, state transitions, scheduling and recipient intent against real migrated SQL.</summary>
/// <param name="database">Fixture-owned isolated database; each case inserts independent principals and resources.</param>
public sealed class QuestServiceTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Creation assigns one equal owner without participation or delivery; draft deletion retains audit only.</summary>
    /// <returns>Completion after persisted state, inherited UTC interval, and deletion assertions.</returns>
    [Fact]
    public async Task DraftCreateEditDelete_PreservesAuditWithoutParticipationOrDelivery()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = await service.CreateAsync(scenario.Seed.Event.Id, scenario.Input() with { Location = "" });
        var detail = await service.GetAsync(id);
        Assert.Equal(QuestStatus.Draft, detail.Summary.Status);
        Assert.Equal(scenario.Seed.User.Id, Assert.Single(detail.Owners).Id);
        Assert.Equal(ParticipationStatus.None, detail.Summary.Participation);
        Assert.Equal(FoundationSeed.Now, detail.Summary.StartUtc);
        Assert.Equal("Europe/Prague", detail.Summary.TimeZoneId);
        await service.EditAsync(id, detail.Summary.Version, scenario.Input() with { Title = "Edited draft" });
        detail = await service.GetAsync(id);
        Assert.Equal("Edited draft", detail.Summary.Title);
        await service.DeleteDraftAsync(id, detail.Summary.Version);
        await using var db = database.CreateContext();
        Assert.False(await db.Quests.AnyAsync(q => q.Id == id));
        Assert.False(await db.QuestOwners.AnyAsync(q => q.QuestId == id));
        Assert.False(await db.Participations.AnyAsync(q => q.QuestId == id));
        Assert.False(await db.OutboxMessages.AnyAsync(q => q.AggregateId == id));
        Assert.Equal(new[] { "ContentEdited", "DraftCreated", "DraftDeleted" },
            await db.AuditEntries.Where(q => q.ResourceId == id).OrderBy(q => q.Action).Select(q => q.Action).ToArrayAsync());
    }

    /// <summary>Publication creates one durable public fan-out and completion intent without joining the creator.</summary>
    /// <returns>Completion after version, lifecycle-history, payload and idempotency assertions.</returns>
    [Fact]
    public async Task Publish_IsAtomicRepeatSafeAndFixesVisibility()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = await service.CreateAsync(scenario.Seed.Event.Id, scenario.Input());
        var before = await service.GetAsync(id);
        await service.ChangeStatusAsync(id, before.Summary.Version, QuestStatus.Active, "");
        var after = await service.GetAsync(id);
        await service.ChangeStatusAsync(id, after.Summary.Version, QuestStatus.Active, "");
        Assert.Equal(QuestStatus.Active, after.Summary.Status);
        Assert.Equal(ParticipationStatus.None, after.Summary.Participation);
        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            service.EditAsync(id, after.Summary.Version, scenario.Input(QuestVisibility.Private)));
        Assert.Equal(ErrorCode.Conflict, failure.Code);
        await using var db = database.CreateContext();
        var outbox = Assert.Single(await db.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync());
        var envelope = JsonSerializer.Deserialize<ChangeEnvelope>(outbox.PayloadJson)!;
        Assert.Equal(NotificationKind.QuestPublished, envelope.Kind);
        Assert.Empty(envelope.RecipientIds);
        Assert.Equal(scenario.Seed.User.Id, envelope.ActorId);
        Assert.Single(await db.QuestStatusHistory.Where(x => x.QuestId == id).ToListAsync());
        Assert.Single(await db.ScheduledWork.Where(x => x.QuestId == id && x.Type == WorkTypes.QuestCompletion).ToListAsync());
        Assert.False(await db.ScheduledWork.AnyAsync(x => x.QuestId == id && x.Type == WorkTypes.Reminder));
    }

    /// <summary>Ordinary and moderation paths independently enforce grants and roster redaction, including an owner using moderation.</summary>
    /// <returns>Completion after private denial, all null projections, and persisted access-audit assertions.</returns>
    [Fact]
    public async Task PrivateModeration_RedactsEveryRosterAndCount_WithoutGrantingOrdinaryAccess()
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var id = scenario.Seed.Quest.Id;
        var owner = scenario.Service();
        await owner.ParticipateAsync(id, ParticipationCommand.Join);
        var moderator = scenario.Service(scenario.Seed.Other);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => moderator.GetAsync(id))).Code);
        var detail = await moderator.GetAsync(id, true);
        Assert.Equal(scenario.Seed.Quest.Title, detail.Summary.Title);
        Assert.Null(detail.Attendees);
        Assert.Null(detail.Followers);
        Assert.Null(detail.Invitees);
        Assert.Null(detail.Summary.AttendeeCount);
        Assert.Null(detail.Summary.FollowerCount);
        Assert.Equal(ParticipationStatus.None, detail.Summary.Participation);
        Assert.False(detail.Summary.IsOwner);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            moderator.EditAsync(id, detail.Summary.Version, scenario.Input(QuestVisibility.Private)))).Code);
        var history = await moderator.HistoryAsync(id, true);
        Assert.DoesNotContain(history, h => h.Action.Contains(scenario.Seed.User.Id.ToString("N"), StringComparison.Ordinal));
        var page = await moderator.ListAsync(QuestListKind.Moderation, scenario.Seed.Event.Id, new());
        Assert.Null(Assert.Single(page.Items).AttendeeCount);
        await owner.AddOwnerAsync(id, scenario.Seed.Other.Id);
        Assert.Null((await moderator.GetAsync(id, true)).Attendees);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.ResourceId == id && a.Action == "ModerationDetailRead"));
        Assert.Single(await db.AuditEntries.Where(a => a.ResourceId == id && a.Action == "ModerationHistoryRead").ToListAsync());
    }

    /// <summary>Draft cancellation never turns unpublished content into ordinary or moderation discovery.</summary>
    /// <returns>Completion after access and history-list privacy assertions.</returns>
    [Fact]
    public async Task CancelledUnpublishedDraft_RemainsOwnerOnly()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var owner = scenario.Service();
        var id = await owner.CreateAsync(scenario.Seed.Event.Id, scenario.Input());
        var detail = await owner.GetAsync(id);
        await owner.ChangeStatusAsync(id, detail.Summary.Version, QuestStatus.Cancelled, "No longer needed.");
        var moderator = scenario.Service(scenario.Seed.Other);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => moderator.GetAsync(id))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => moderator.GetAsync(id, true))).Code);
        Assert.DoesNotContain((await moderator.ListAsync(QuestListKind.History, scenario.Seed.Event.Id, new())).Items, q => q.Id == id);
        Assert.Equal(QuestStatus.Cancelled, (await owner.GetAsync(id)).Summary.Status);
    }

    /// <summary>A private invitation immediately grants read access, independently of ownership and participation, with repeat-safe withdrawals.</summary>
    /// <returns>Completion after participation, invitation, durable withdrawal and independent-owner access assertions.</returns>
    [Fact]
    public async Task InviteRevokeReinvite_PreservesIndependentOwnerAccess_AndNeverRestoresParticipation()
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var service = scenario.Service();
        var guest = scenario.Service(scenario.Seed.Other);
        var id = scenario.Seed.Quest.Id;
        await service.InviteAsync(id, scenario.Seed.Other.Id);
        await service.InviteAsync(id, scenario.Seed.Other.Id);
        var invited = await guest.GetAsync(id);
        Assert.Equal(ParticipationStatus.None, invited.Summary.Participation);
        Assert.Null(invited.Followers);
        Assert.Null(invited.Invitees);
        Assert.Single((await guest.ListAsync(QuestListKind.Invited, null, new())).Items);
        await guest.ParticipateAsync(id, ParticipationCommand.Join);
        await service.AddOwnerAsync(id, scenario.Seed.Other.Id);
        await service.RevokeInvitationAsync(id, scenario.Seed.Other.Id, "The access list changed.");
        var stillOwner = await guest.GetAsync(id);
        Assert.True(stillOwner.Summary.IsOwner);
        Assert.Equal(ParticipationStatus.None, stillOwner.Summary.Participation);
        await guest.ParticipateAsync(id, ParticipationCommand.Join);
        await service.RevokeInvitationAsync(id, scenario.Seed.Other.Id, "The access list changed.");
        Assert.Equal(ParticipationStatus.Joined, (await guest.GetAsync(id)).Summary.Participation);
        await service.RemoveOwnerAsync(id, scenario.Seed.Other.Id);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => guest.GetAsync(id))).Code);
        await service.InviteAsync(id, scenario.Seed.Other.Id);
        Assert.Equal(ParticipationStatus.None, (await guest.GetAsync(id)).Summary.Participation);
        await using var db = database.CreateContext();
        Assert.Single(await db.QuestInvitations.Where(x => x.QuestId == id).ToListAsync());
        var changes = (await db.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync())
            .Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!).ToArray();
        Assert.Equal(2, changes.Count(x => x.Kind == NotificationKind.QuestInvitation));
        var withdrawal = changes.First(x => x.Kind == NotificationKind.AccessRemoved);
        Assert.Equal(new[] { scenario.Seed.Other.Id }, withdrawal.PreviousAttendeeIds);
        Assert.True(withdrawal.CalendarChanged);
    }

    /// <summary>Join replaces Following atomically, repeats produce no duplicate work, capacity is advisory, and leave never restores Following.</summary>
    /// <returns>Completion after exact states, counts, revisions and durable envelope assertions.</returns>
    [Fact]
    public async Task Participation_IsExclusiveIdempotent_AndCapacityIsAdvisory()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var other = scenario.Service(scenario.Seed.Other);
        var id = scenario.Seed.Quest.Id;
        var before = await service.GetAsync(id);
        await service.EditAsync(id, before.Summary.Version, scenario.Input());
        await service.ParticipateAsync(id, ParticipationCommand.Follow);
        await service.ParticipateAsync(id, ParticipationCommand.Follow);
        Assert.Equal(1, (await service.GetAsync(id)).Summary.FollowerCount);
        await service.ParticipateAsync(id, ParticipationCommand.Join);
        await service.ParticipateAsync(id, ParticipationCommand.Join);
        await other.ParticipateAsync(id, ParticipationCommand.Join);
        var joined = await service.GetAsync(id);
        Assert.Equal(2, joined.Summary.AttendeeCount);
        Assert.Equal(0, joined.Summary.FollowerCount);
        Assert.Equal(1, joined.Summary.SuggestedCapacity);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            service.ParticipateAsync(id, ParticipationCommand.Follow))).Code);
        await service.ParticipateAsync(id, ParticipationCommand.Unfollow);
        Assert.Equal(ParticipationStatus.Joined, (await service.GetAsync(id)).Summary.Participation);
        await service.ParticipateAsync(id, ParticipationCommand.Leave);
        await service.ParticipateAsync(id, ParticipationCommand.Leave);
        Assert.Equal(ParticipationStatus.None, (await service.GetAsync(id)).Summary.Participation);
        await service.ParticipateAsync(id, ParticipationCommand.Follow);
        await service.ParticipateAsync(id, ParticipationCommand.Leave);
        Assert.Equal(ParticipationStatus.Following, (await service.GetAsync(id)).Summary.Participation);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.Participations.CountAsync(p => p.QuestId == id));
        var changes = (await db.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync())
            .Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!).ToArray();
        Assert.Equal(2, changes.Count(x => x.Kind == NotificationKind.Joined));
        Assert.Single(changes, x => x.Kind == NotificationKind.Left);
        foreach (var change in changes.Where(x => x.Kind is NotificationKind.Joined or NotificationKind.Left))
        {
            Assert.Equal(new[] { change.ActorId!.Value }, change.AffectedUserIds);
            Assert.Contains(change.AffectedUserIds![0], change.RecipientIds);
        }
        Assert.Equal(3, changes.Where(x => x.Kind is NotificationKind.Joined or NotificationKind.Left)
            .Select(x => x.CalendarRevision).Distinct().Count());
    }

    /// <summary>Owner removal needs a meaningful reason but never bans the removed member from rejoining a public Quest.</summary>
    /// <returns>Completion after validation rollback, rejoin and recipient-only withdrawal assertions.</returns>
    [Fact]
    public async Task AttendeeRemoval_RequiresReason_AndPublicRejoinIsAllowed()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var owner = scenario.Service();
        var attendee = scenario.Service(scenario.Seed.Other);
        var id = scenario.Seed.Quest.Id;
        await attendee.ParticipateAsync(id, ParticipationCommand.Join);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            owner.RemoveAttendeeAsync(id, scenario.Seed.Other.Id, "short"))).Code);
        Assert.Equal(ParticipationStatus.Joined, (await attendee.GetAsync(id)).Summary.Participation);
        await owner.RemoveAttendeeAsync(id, scenario.Seed.Other.Id, "Please choose another session.");
        await owner.RemoveAttendeeAsync(id, scenario.Seed.Other.Id, "Please choose another session.");
        Assert.Equal(ParticipationStatus.None, (await attendee.GetAsync(id)).Summary.Participation);
        await attendee.ParticipateAsync(id, ParticipationCommand.Join);
        Assert.Equal(ParticipationStatus.Joined, (await attendee.GetAsync(id)).Summary.Participation);
        await using var db = database.CreateContext();
        var changes = (await db.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync())
            .Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!).ToArray();
        var removed = Assert.Single(changes, x => x.Kind == NotificationKind.AttendeeRemoved);
        Assert.Equal(new[] { scenario.Seed.Other.Id }, removed.PreviousAttendeeIds);
        Assert.Equal(new[] { scenario.Seed.Other.Id }, removed.AffectedUserIds);
        Assert.Equal("Please choose another session.", removed.Reason);
    }

    /// <summary>Same-instant participation actions retain distinct serialized targets after later rejoin/follow changes overwrite current participation facts.</summary>
    /// <param name="withdrawalKind">The self-service leave, owner removal, or private access-loss producer whose captured targets must remain stable.</param>
    /// <returns>Completion after exact target/observer, withdrawal, revision and immutable persisted-payload assertions.</returns>
    [Theory]
    [InlineData(NotificationKind.Left)]
    [InlineData(NotificationKind.AttendeeRemoved)]
    [InlineData(NotificationKind.AccessRemoved)]
    public async Task SameInstantActions_PreserveCapturedTargets_AfterLaterParticipationChanges(NotificationKind withdrawalKind)
    {
        var privateQuest = withdrawalKind == NotificationKind.AccessRemoved;
        var scenario = await QuestScenario.CreateAsync(database, privateQuest);
        var second = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, second);
        await FoundationSeed.PersistAsync(database, scenario.Seed.Membership(second.Id));
        var owner = scenario.Service();
        var firstParticipant = scenario.Service(scenario.Seed.Other);
        var secondParticipant = scenario.Service(second);
        var id = scenario.Seed.Quest.Id;
        var occurred = scenario.Clock.Now;
        if (privateQuest)
        {
            await owner.InviteAsync(id, scenario.Seed.Other.Id);
            await owner.InviteAsync(id, second.Id);
        }
        await firstParticipant.ParticipateAsync(id, ParticipationCommand.Join);
        await secondParticipant.ParticipateAsync(id, ParticipationCommand.Join);
        foreach (var participant in new[] { scenario.Seed.Other, second })
        {
            if (withdrawalKind == NotificationKind.Left)
                await scenario.Service(participant).ParticipateAsync(id, ParticipationCommand.Leave);
            else if (privateQuest)
                await owner.RevokeInvitationAsync(id, participant.Id, "The access list changed.");
            else
                await owner.RemoveAttendeeAsync(id, participant.Id, "Please choose another session.");
        }

        Dictionary<Guid, string> captured;
        await using (var db = database.CreateContext())
            captured = await db.OutboxMessages.Where(o => o.AggregateId == id).ToDictionaryAsync(o => o.Id, o => o.PayloadJson);

        scenario.Clock.Now = occurred.AddMinutes(1);
        if (privateQuest)
        {
            await owner.InviteAsync(id, scenario.Seed.Other.Id);
            await owner.InviteAsync(id, second.Id);
        }
        await firstParticipant.ParticipateAsync(id, ParticipationCommand.Join);
        await secondParticipant.ParticipateAsync(id, ParticipationCommand.Follow);

        await using var read = database.CreateContext();
        var participation = await read.Participations.Where(p => p.QuestId == id).ToDictionaryAsync(p => p.UserId);
        Assert.Equal(2, participation.Count);
        Assert.Equal(ParticipationStatus.Joined, participation[scenario.Seed.Other.Id].Status);
        Assert.Equal(ParticipationStatus.Following, participation[second.Id].Status);
        Assert.All(participation.Values, p => Assert.Equal(occurred.AddMinutes(1), p.ChangedUtc));
        var persisted = await read.OutboxMessages.Where(o => o.AggregateId == id).ToDictionaryAsync(o => o.Id, o => o.PayloadJson);
        Assert.All(captured, entry => Assert.Equal(entry.Value, persisted[entry.Key]));
        var changes = captured.Values.Select(json => Assert.IsType<ChangeEnvelope>(JsonSerializer.Deserialize<ChangeEnvelope>(json)))
            .Where(change => change.Kind == NotificationKind.Joined || change.Kind == withdrawalKind)
            .OrderBy(change => change.CalendarRevision).ToArray();
        Assert.Collection(changes,
            change => VerifyTarget(change, NotificationKind.Joined, scenario.Seed.Other.Id, 1),
            change => VerifyTarget(change, NotificationKind.Joined, second.Id, 2),
            change => VerifyTarget(change, withdrawalKind, scenario.Seed.Other.Id, 3),
            change => VerifyTarget(change, withdrawalKind, second.Id, 4));

        void VerifyTarget(ChangeEnvelope change, NotificationKind kind, Guid target, long revisionOffset)
        {
            Assert.Equal(kind, change.Kind);
            Assert.Equal(new[] { target }, change.AffectedUserIds);
            Assert.Equal(new[] { scenario.Seed.User.Id, target }.OrderBy(user => user), change.RecipientIds.OrderBy(user => user));
            Assert.Equal(kind is NotificationKind.Joined or NotificationKind.Left ? target : scenario.Seed.User.Id, change.ActorId);
            Assert.Equal(kind == NotificationKind.Joined ? Array.Empty<Guid>() : [target], change.PreviousAttendeeIds);
            Assert.Equal(occurred, change.OccurredUtc);
            Assert.Equal(scenario.Seed.Quest.CalendarRevision + revisionOffset, change.CalendarRevision);
            Assert.True(change.CalendarChanged);
        }
    }

    /// <summary>Suspended edits preserve status and withdrawn calendars; reinstatement alone restores them and archive retains history.</summary>
    /// <returns>Completion after lifecycle, revision, recipient and retained-state participation assertions.</returns>
    [Fact]
    public async Task SuspendEditReinstateCancelArchive_PreservesLifecycleAndDeliveryRules()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var owner = scenario.Service();
        var moderator = scenario.Service(scenario.Seed.Other);
        var id = scenario.Seed.Quest.Id;
        await owner.ParticipateAsync(id, ParticipationCommand.Join);
        var detail = await owner.GetAsync(id);
        await moderator.ChangeStatusAsync(id, detail.Summary.Version, QuestStatus.Suspended, "Please correct the location.");
        detail = await owner.GetAsync(id);
        Assert.Equal(QuestStatus.Suspended, detail.Summary.Status);
        await owner.EditAsync(id, detail.Summary.Version, scenario.Input() with { Location = "Corrected room" });
        detail = await owner.GetAsync(id);
        Assert.Equal(QuestStatus.Suspended, detail.Summary.Status);
        Assert.Equal("Corrected room", detail.Summary.Location);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            owner.ChangeStatusAsync(id, detail.Summary.Version, QuestStatus.Active, "I corrected the room."))).Code);
        await moderator.ChangeStatusAsync(id, detail.Summary.Version, QuestStatus.Active, "The corrected room is approved.");
        detail = await owner.GetAsync(id);
        await owner.ChangeStatusAsync(id, detail.Summary.Version, QuestStatus.Cancelled, "The session is no longer needed.");
        detail = await owner.GetAsync(id);
        await owner.ChangeStatusAsync(id, detail.Summary.Version, QuestStatus.Archived, "");
        await owner.ParticipateAsync(id, ParticipationCommand.Leave);
        Assert.Equal(ParticipationStatus.None, (await owner.GetAsync(id)).Summary.Participation);
        await using var db = database.CreateContext();
        var changes = (await db.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync())
            .Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!).ToArray();
        var edited = Assert.Single(changes, x => x.Kind == NotificationKind.SuspendedQuestEdited);
        Assert.False(edited.CalendarChanged);
        Assert.Contains(scenario.Seed.Other.Id, edited.RecipientIds);
        var reinstated = Assert.Single(changes, x => x.Kind == NotificationKind.QuestReinstated);
        Assert.True(reinstated.CalendarChanged);
        Assert.True(reinstated.CalendarRevision > edited.CalendarRevision);
        Assert.Equal(new[] { scenario.Seed.User.Id }, reinstated.PreviousAttendeeIds);
        Assert.Equal(4, await db.QuestStatusHistory.CountAsync(x => x.QuestId == id));
    }

    /// <summary>Stale edits and invalid scheduling leave content, audit, history and durable work unchanged.</summary>
    /// <returns>Completion after exact conflict/validation and no-partial-write assertions.</returns>
    [Fact]
    public async Task FailedMutation_RollsBackAuditOutboxAndContent()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = scenario.Seed.Quest.Id;
        var old = await service.GetAsync(id);
        await service.EditAsync(id, old.Summary.Version, scenario.Input() with { Title = "First valid edit" });
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            service.EditAsync(id, old.Summary.Version, scenario.Input() with { Title = "Stale overwrite" }))).Code);
        var current = await service.GetAsync(id);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            service.EditAsync(id, current.Summary.Version, scenario.Input() with { EndLocal = new DateTime(2026, 7, 17, 1, 0, 0) }))).Code);
        await using var db = database.CreateContext();
        Assert.Equal("First valid edit", (await db.Quests.SingleAsync(q => q.Id == id)).Title);
        Assert.Single(await db.AuditEntries.Where(x => x.ResourceId == id).ToListAsync());
        Assert.Single(await db.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync());
        Assert.Empty(await db.QuestStatusHistory.Where(x => x.QuestId == id).ToListAsync());
    }

    /// <summary>Every service call rechecks persisted actor eligibility and membership, including a privileged administrator.</summary>
    /// <param name="loss">Eligibility, departure, or membership loss partition.</param>
    /// <returns>Completion after all public query and representative command denials.</returns>
    [Theory]
    [InlineData("eligibility")]
    [InlineData("departure")]
    [InlineData("membership")]
    public async Task CurrentActorLoss_DeniesQueriesAndCommands_DespiteOwnershipAndAdministrator(string loss)
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var service = scenario.Service();
        await FoundationSeed.PersistAsync(database, new Administrator { UserId = scenario.Seed.User.Id });
        await service.GetAsync(scenario.Seed.Quest.Id);
        await using (var db = database.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == scenario.Seed.User.Id);
            if (loss == "eligibility") user.IsEligible = false;
            if (loss == "departure") user.DepartureVerifiedUtc = scenario.Clock.Now;
            if (loss == "membership")
                (await db.EventMemberships.SingleAsync(m => m.EventId == scenario.Seed.Event.Id && m.UserId == user.Id)).Status = MembershipStatus.Removed;
            await db.SaveChangesAsync();
        }
        var expected = loss == "membership" ? ErrorCode.NotFound : ErrorCode.Forbidden;
        Assert.Equal(expected, (await Assert.ThrowsAsync<DomainException>(() => service.GetAsync(scenario.Seed.Quest.Id))).Code);
        Assert.Equal(expected, (await Assert.ThrowsAsync<DomainException>(() => service.HistoryAsync(scenario.Seed.Quest.Id))).Code);
        Assert.Equal(expected, (await Assert.ThrowsAsync<DomainException>(() => service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join))).Code);
        if (loss == "membership")
        {
            Assert.Empty(await service.GetOfflineJoinedAsync());
            Assert.Empty((await service.ListAsync(QuestListKind.Organizing, null, new())).Items);
        }
        else
        {
            Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => service.GetOfflineJoinedAsync())).Code);
            Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => service.ListAsync(QuestListKind.Organizing, null, new()))).Code);
        }
    }

    /// <summary>Owner-only and invited/followed-only resources never enter the minimal joined snapshot.</summary>
    /// <returns>Completion after joined identity, minimal shape, and leave-removal assertions.</returns>
    [Fact]
    public async Task OfflineSnapshot_IsJoinedOnlyAndMinimal()
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var service = scenario.Service();
        Assert.Empty(await service.GetOfflineJoinedAsync());
        await service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Follow);
        Assert.Empty(await service.GetOfflineJoinedAsync());
        await service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
        var snapshot = Assert.Single(await service.GetOfflineJoinedAsync());
        Assert.Equal(scenario.Seed.Quest.Id, snapshot.Id);
        Assert.Equal(scenario.Seed.Quest.Title, snapshot.Title);
        Assert.Equal("Europe/Prague", snapshot.TimeZoneId);
        Assert.Equal(new[] { "EndUtc", "EventId", "Id", "Location", "StartUtc", "Status", "TimeZoneId", "Title" },
            typeof(OfflineQuest).GetProperties().Select(p => p.Name).Order().ToArray());
        await service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Leave);
        Assert.Empty(await service.GetOfflineJoinedAsync());
    }

    /// <summary>Content edits allocate exact calendar/start revisions and captured recipient intent without independently scheduling reminders.</summary>
    /// <returns>Completion after title, capacity, start, and end edits assert their independent revision/flag behavior.</returns>
    [Fact]
    public async Task Edits_AllocateCalendarAndStartRevisions_WithExactDeliveryFlags()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = scenario.Seed.Quest.Id;
        await service.ParticipateAsync(id, ParticipationCommand.Follow);
        await scenario.Service(scenario.Seed.Other).ParticipateAsync(id, ParticipationCommand.Join);
        var detail = await service.GetAsync(id);
        var input = scenario.Input() with
        {
            Title = detail.Summary.Title + " edited",
            Description = detail.Description,
            Location = detail.Summary.Location,
            SuggestedCapacity = detail.Summary.SuggestedCapacity
        };
        scenario.Clock.Now = scenario.Clock.Now.AddSeconds(1);
        await service.EditAsync(id, detail.Summary.Version, input);
        detail = await service.GetAsync(id);
        input = input with { SuggestedCapacity = 2 };
        scenario.Clock.Now = scenario.Clock.Now.AddSeconds(1);
        await service.EditAsync(id, detail.Summary.Version, input);
        detail = await service.GetAsync(id);
        input = input with { StartLocal = input.StartLocal.AddMinutes(15) };
        scenario.Clock.Now = scenario.Clock.Now.AddSeconds(1);
        await service.EditAsync(id, detail.Summary.Version, input);
        detail = await service.GetAsync(id);
        input = input with { EndLocal = input.EndLocal.AddMinutes(15) };
        scenario.Clock.Now = scenario.Clock.Now.AddSeconds(1);
        await service.EditAsync(id, detail.Summary.Version, input);

        await using var db = database.CreateContext();
        var quest = await db.Quests.SingleAsync(q => q.Id == id);
        Assert.Equal(1, quest.StartRevision);
        Assert.Equal(11, quest.CalendarRevision);
        Assert.Equal(FoundationSeed.Now.AddMinutes(15), quest.StartUtc);
        Assert.Equal(FoundationSeed.Now.AddHours(2).AddMinutes(15), quest.EndUtc);
        var changes = (await db.OutboxMessages.Where(o => o.AggregateId == id)
            .OrderBy(o => o.OccurredUtc).ToListAsync())
            .Select(o => JsonSerializer.Deserialize<ChangeEnvelope>(o.PayloadJson)!)
            .Where(c => c.Kind == NotificationKind.QuestUpdated).ToArray();
        Assert.Equal(new long[] { 9, 9, 10, 11 }, changes.Select(c => c.CalendarRevision).ToArray());
        Assert.Equal(new[] { true, false, true, true }, changes.Select(c => c.CalendarChanged).ToArray());
        Assert.Equal(new[] { false, false, true, true }, changes.Select(c => c.MaterialChange).ToArray());
        foreach (var change in changes)
        {
            Assert.Equal(new[] { scenario.Seed.Other.Id }, change.PreviousAttendeeIds);
            Assert.Equal(new[] { scenario.Seed.User.Id, scenario.Seed.Other.Id }.Order(),
                change.RecipientIds.Order());
        }
        Assert.Single(await db.ScheduledWork.Where(w => w.QuestId == id && w.Type == WorkTypes.QuestCompletion).ToListAsync());
        Assert.False(await db.ScheduledWork.AnyAsync(w => w.QuestId == id && w.Type == WorkTypes.Reminder));
    }

    /// <summary>Unpublished cancellation permits an omitted reason and never creates participant delivery.</summary>
    /// <returns>Completion after draft cancellation state, empty explanation and absent outbox assertions.</returns>
    [Fact]
    public async Task DraftCancellation_AllowsOmittedReason_WithoutParticipantDelivery()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = await service.CreateAsync(scenario.Seed.Event.Id, scenario.Input());
        await service.ChangeStatusAsync(id, (await service.GetAsync(id)).Summary.Version, QuestStatus.Cancelled, "");
        var detail = await service.GetAsync(id);
        Assert.Equal(QuestStatus.Cancelled, detail.Summary.Status);
        Assert.Equal("", detail.StatusReason);
        await using var db = database.CreateContext();
        Assert.False(await db.OutboxMessages.AnyAsync(o => o.AggregateId == id));
    }

    /// <summary>Archiving cannot permanently freeze owner-derived private access; removal retains another eligible owner and ends lost-access participation.</summary>
    /// <returns>Completion after archived status, revoked owner access, continuity and participation assertions.</returns>
    [Fact]
    public async Task ArchivedOwnership_RemainsRevocable_WithoutAllowingNewOwnerGrants()
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var service = scenario.Service();
        var id = scenario.Seed.Quest.Id;
        await service.AddOwnerAsync(id, scenario.Seed.Other.Id);
        await scenario.Service(scenario.Seed.Other).ParticipateAsync(id, ParticipationCommand.Join);
        await service.ChangeStatusAsync(id, (await service.GetAsync(id)).Summary.Version, QuestStatus.Cancelled, "The session is cancelled.");
        await service.ChangeStatusAsync(id, (await service.GetAsync(id)).Summary.Version, QuestStatus.Archived, "");
        await service.RemoveOwnerAsync(id, scenario.Seed.Other.Id);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service(scenario.Seed.Other).GetAsync(id))).Code);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            service.AddOwnerAsync(id, scenario.Seed.Other.Id))).Code);
        var detail = await service.GetAsync(id);
        Assert.Equal(QuestStatus.Archived, detail.Summary.Status);
        Assert.Equal(scenario.Seed.User.Id, Assert.Single(detail.Owners).Id);
        await using var db = database.CreateContext();
        Assert.Equal(ParticipationStatus.None, (await db.Participations.SingleAsync(p => p.QuestId == id)).Status);
    }
}
