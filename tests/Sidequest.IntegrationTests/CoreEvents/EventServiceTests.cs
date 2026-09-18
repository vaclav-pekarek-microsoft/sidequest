using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Exercises Event commands against migrated SQL with fixed time, real authorization, and enlisted child effects.</summary>
/// <param name="database">Uniquely owned migrated database, shared only by this sequential test class.</param>
public sealed class EventServiceTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Coalesces registered members and affected unregistered child audiences before the cascade, without duplicating overlapping roles.</summary>
    /// <returns>A task completing after exact eligible-recipient, privacy, and persisted parent/child cancellation checks.</returns>
    [Fact]
    public async Task CancellationCoalescesRegisteredAndAffectedQuestAudiences()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var people = new[] { "registered", "owner", "invitee", "follower", "attendee", "unrelated",
            "revoked", "removed", "ineligible", "departed", "foreign" }.ToDictionary(name => name, name =>
        {
            var person = FoundationSeed.NewUser();
            person.TenantId = seed.User.TenantId;
            person.DisplayName = name;
            return person;
        });
        people["registered"].LastSignedInUtc = FoundationSeed.Now;
        people["ineligible"].IsEligible = false;
        people["departed"].DepartureVerifiedUtc = FoundationSeed.Now;
        people["foreign"].TenantId = Guid.NewGuid();
        await FoundationSeed.PersistAsync(database, people.Values.Cast<Entity>().ToArray());
        await FoundationSeed.PersistAsync(database, people.Select(pair => seed.Membership(pair.Value.Id,
            pair.Key == "removed" ? MembershipStatus.Removed : MembershipStatus.Active)).ToArray());
        var second = FoundationSeed.NewQuest(seed.Event.Id, seed.User.Id);
        second.Status = QuestStatus.Suspended;
        second.Visibility = QuestVisibility.Private;
        second.Title = "Private child cancellation sentinel";
        await FoundationSeed.PersistAsync(database, second,
            new QuestOwner { QuestId = seed.Quest.Id, UserId = people["owner"].Id },
            new QuestOwner { QuestId = second.Id, UserId = people["attendee"].Id },
            new QuestInvitation { QuestId = seed.Quest.Id, UserId = people["invitee"].Id, InvitedById = seed.User.Id, Status = QuestInvitationStatus.Active, ChangedUtc = FoundationSeed.Now },
            new QuestInvitation { QuestId = second.Id, UserId = people["invitee"].Id, InvitedById = seed.User.Id, Status = QuestInvitationStatus.Active, ChangedUtc = FoundationSeed.Now },
            new QuestInvitation { QuestId = seed.Quest.Id, UserId = people["revoked"].Id, InvitedById = seed.User.Id, Status = QuestInvitationStatus.Revoked, ChangedUtc = FoundationSeed.Now },
            new QuestParticipation { QuestId = seed.Quest.Id, UserId = people["follower"].Id, Status = ParticipationStatus.Following, ChangedUtc = FoundationSeed.Now },
            new QuestParticipation { QuestId = second.Id, UserId = people["follower"].Id, Status = ParticipationStatus.Following, ChangedUtc = FoundationSeed.Now },
            new QuestParticipation { QuestId = seed.Quest.Id, UserId = people["attendee"].Id, Status = ParticipationStatus.Joined, ChangedUtc = FoundationSeed.Now });
        await FoundationSeed.PersistAsync(database, new[] { "removed", "ineligible", "departed", "foreign" }
            .Select(name => new QuestParticipation
            {
                QuestId = seed.Quest.Id, UserId = people[name].Id,
                Status = ParticipationStatus.Following, ChangedUtc = FoundationSeed.Now
            }).ToArray());
        var service = context.Service(seed.User);
        await service.ChangeStatusAsync(seed.Event.Id, (await service.GetAsync(seed.Event.Id)).Summary.Version,
            EventStatus.Cancelled, "The Event has been cancelled.");
        await using var read = database.CreateContext();
        var message = await read.OutboxMessages.SingleAsync(x => x.AggregateId == seed.Event.Id);
        var change = JsonSerializer.Deserialize<ChangeEnvelope>(message.PayloadJson)!;
        var expected = new[] { "registered", "owner", "invitee", "follower", "attendee" }.Select(name => people[name].Id).Order();
        Assert.Equal(expected, change.RecipientIds.Order());
        Assert.Equal(NotificationKind.EventCancelled, change.Kind);
        Assert.Equal(seed.User.Id, change.ActorId);
        Assert.Equal(FoundationSeed.Now, change.OccurredUtc);
        Assert.Null(change.QuestId);
        Assert.Null(change.PreviousAttendeeIds);
        Assert.Equal(0, change.CalendarRevision);
        Assert.DoesNotContain(second.Title, message.PayloadJson);
        Assert.DoesNotContain(second.Description, message.PayloadJson);
        Assert.Equal(EventStatus.Cancelled, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(2, await read.Quests.CountAsync(x => x.EventId == seed.Event.Id && x.Status == QuestStatus.Cancelled));
        Assert.Single(context.Quests.Calls);
    }

    /// <summary>Additional cancellation fanout excludes drafts, terminal children, and the exact effective-completion boundary.</summary>
    /// <param name="status">Persisted child lifecycle state before parent cancellation.</param>
    /// <param name="endOffsetTicks">Child end relative to the operation instant.</param>
    /// <param name="affected">Whether the unregistered member's child roles receive the coalesced parent notice.</param>
    /// <returns>A task completing after the exact captured parent audience assertion.</returns>
    [Theory]
    [InlineData(QuestStatus.Draft, 1, false)]
    [InlineData(QuestStatus.Completed, 1, false)]
    [InlineData(QuestStatus.Cancelled, 1, false)]
    [InlineData(QuestStatus.Archived, 1, false)]
    [InlineData(QuestStatus.Active, -1, false)]
    [InlineData(QuestStatus.Active, 0, false)]
    [InlineData(QuestStatus.Active, 1, true)]
    [InlineData(QuestStatus.Suspended, -1, false)]
    [InlineData(QuestStatus.Suspended, 0, false)]
    [InlineData(QuestStatus.Suspended, 1, true)]
    public async Task CancellationAudienceHonorsChildLifecycleBoundary(QuestStatus status, long endOffsetTicks, bool affected)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        await FoundationSeed.PersistAsync(database, seed.Membership(seed.Other.Id),
            new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.Other.Id },
            new QuestInvitation { QuestId = seed.Quest.Id, UserId = seed.Other.Id, InvitedById = seed.User.Id, Status = QuestInvitationStatus.Active, ChangedUtc = FoundationSeed.Now },
            new QuestParticipation { QuestId = seed.Quest.Id, UserId = seed.Other.Id, Status = ParticipationStatus.Following, ChangedUtc = FoundationSeed.Now });
        await using (var setup = database.CreateContext())
        {
            var quest = await setup.Quests.SingleAsync(x => x.Id == seed.Quest.Id);
            quest.StartUtc = FoundationSeed.Now.AddMinutes(-1);
            quest.EndUtc = FoundationSeed.Now.AddTicks(endOffsetTicks);
            quest.Status = status;
            await setup.SaveChangesAsync();
        }
        var service = context.Service(seed.User);
        await service.ChangeStatusAsync(seed.Event.Id, (await service.GetAsync(seed.Event.Id)).Summary.Version,
            EventStatus.Cancelled, "The Event has been cancelled.");
        await using var read = database.CreateContext();
        var message = await read.OutboxMessages.SingleAsync(x => x.AggregateId == seed.Event.Id);
        var change = JsonSerializer.Deserialize<ChangeEnvelope>(message.PayloadJson)!;
        Assert.Equal(NotificationKind.EventCancelled, change.Kind);
        Assert.Equal(affected ? new[] { seed.Other.Id } : [], change.RecipientIds);
        Assert.Null(change.QuestId);
        Assert.Equal(0, change.CalendarRevision);
    }

    /// <summary>Creates a trimmed unpublished aggregate with exactly one creator owner/member and no delivery or scheduled work.</summary>
    /// <returns>A task completing after independent persisted-state checks.</returns>
    [Fact]
    public async Task CreateDraftAtomicallyAssignsCreatorWithoutNotifications()
    {
        var context = new EventTestContext(database);
        var user = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, user);
        var id = await context.Service(user).CreateAsync(EventTestContext.Input());
        await using var read = database.CreateContext();
        var item = await read.Events.SingleAsync(x => x.Id == id);
        Assert.Equal("New Event", item.Name);
        Assert.Equal("Member-only details", item.Description);
        Assert.Equal(EventStatus.Draft, item.Status);
        Assert.Equal(user.Id, item.CreatorId);
        Assert.Equal(FoundationSeed.Now, item.CreatedUtc);
        Assert.Equal(user.Id, (await read.EventOwners.SingleAsync(x => x.EventId == id)).UserId);
        var membership = await read.EventMemberships.SingleAsync(x => x.EventId == id);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(user.Id, membership.ChangedById);
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync());
        Assert.Empty(await read.ScheduledWork.Where(x => x.PayloadJson.Contains(id.ToString())).ToListAsync());
        Assert.Equal("Event.Created", (await read.AuditEntries.SingleAsync(x => x.ResourceId == id)).Action);
        Assert.Empty(context.Quests.Calls);
    }

    /// <summary>Exposes only active discovery to a nonmember and denies draft, historical detail and roster reads.</summary>
    /// <returns>A task completing after direct and paged privacy checks.</returns>
    [Fact]
    public async Task NonmemberSeesSummaryButNotDraftHistoryOrRoster()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var draft = FoundationSeed.NewEvent(seed.User.Id);
        draft.Status = EventStatus.Draft;
        var history = FoundationSeed.NewEvent(seed.User.Id);
        history.Status = EventStatus.Completed;
        await FoundationSeed.PersistAsync(database, draft, history);
        var sut = context.Service(seed.Other);
        var detail = await sut.GetAsync(seed.Event.Id);
        Assert.Equal(seed.Event.DiscoverySummary, detail.Summary.DiscoverySummary);
        Assert.Equal(seed.User.Id, Assert.Single(detail.Summary.Owners).Id);
        Assert.Null(detail.Description);
        Assert.Empty(detail.Summary.Version);
        Assert.False(detail.Summary.IsMember);
        Assert.False(detail.Summary.IsOwner);
        foreach (var id in new[] { draft.Id, history.Id })
            Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.GetAsync(id))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.ListMembersAsync(seed.Event.Id, new()))).Code);
        var available = await sut.ListAsync(EventListKind.Available, new(1, 100));
        Assert.Contains(available.Items, x => x.Id == seed.Event.Id);
        Assert.DoesNotContain(available.Items, x => x.Id == draft.Id || x.Id == history.Id);
        Assert.Empty((await sut.ListAsync(EventListKind.History, new())).Items);
    }

    /// <summary>Checks stale edits, immutable published time zone, and child containment without changing persisted configuration.</summary>
    /// <returns>A task completing after valid edit and three rejected writes.</returns>
    [Fact]
    public async Task EditEnforcesRowversionPublishedZoneAndChildContainment()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var sut = context.Service(seed.User);
        var original = Convert.ToBase64String(seed.Event.Version);
        await sut.EditAsync(seed.Event.Id, original, EventTestContext.Input("Updated Event"));
        var current = await sut.GetAsync(seed.Event.Id);
        Assert.Equal("Updated Event", current.Summary.Name);
        Assert.NotEqual(original, current.Summary.Version);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            sut.EditAsync(seed.Event.Id, original, EventTestContext.Input("Lost update")))).Code);
        var zone = await Assert.ThrowsAsync<DomainException>(() =>
            sut.EditAsync(seed.Event.Id, current.Summary.Version, EventTestContext.Input() with { TimeZoneId = "Etc/UTC" }));
        Assert.Equal(ErrorCode.Validation, zone.Code);
        Assert.Equal("TimeZoneId", zone.Field);
        var containment = await Assert.ThrowsAsync<DomainException>(() =>
            sut.EditAsync(seed.Event.Id, current.Summary.Version, EventTestContext.Input() with
            {
                StartDate = new(2026, 7, 16), EndDate = new(2026, 7, 17)
            }));
        Assert.Equal(ErrorCode.Validation, containment.Code);
        Assert.Equal("EndDate", containment.Field);
        Assert.Equal("These dates would exclude an existing Quest.", containment.Message);
        await using var read = database.CreateContext();
        var saved = await read.Events.SingleAsync(x => x.Id == seed.Event.Id);
        Assert.Equal("Updated Event", saved.Name);
        Assert.Equal("Europe/Prague", saved.TimeZoneId);
        Assert.Equal(new DateOnly(2026, 7, 15), saved.StartDate);
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Event.Edited").ToListAsync());
        Assert.Single(await read.ScheduledWork.Where(x => x.PayloadJson.Contains(seed.Event.Id.ToString())).ToListAsync());
    }

    /// <summary>Rolls back already-flushed child SQL and all parent effects when the enlisted cascade fails or is cancelled.</summary>
    /// <param name="cancel">Whether the child raises cooperative cancellation instead of a dependency failure.</param>
    /// <returns>A task completing after the fresh-context rollback assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCascadeFailureRollsBackChildAndParent(bool cancel)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        context.Quests.FailureAfterChildSave = cancel
            ? new OperationCanceledException("Controlled child cancellation")
            : new InvalidOperationException("Controlled child failure");
        var error = await Record.ExceptionAsync(() => context.Service(seed.User).ChangeStatusAsync(seed.Event.Id,
            Convert.ToBase64String(seed.Event.Version), EventStatus.Cancelled, "Owner cancelled"));
        Assert.Same(context.Quests.FailureAfterChildSave, error);
        var call = Assert.Single(context.Quests.Calls);
        Assert.Equal(("Cancel", seed.Event.Id, (Guid?)null, (Guid?)seed.User.Id, "Owner cancelled", FoundationSeed.Now), call);
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Active, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Active, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
        Assert.Empty(await read.EventStatusHistory.Where(x => x.EventId == seed.Event.Id).ToListAsync());
        Assert.Empty(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id).ToListAsync());
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync());
    }

    /// <summary>Projects completion exactly at the exclusive end and reconciles persisted parent and child state before denying new activity.</summary>
    /// <returns>A task completing after before/at-boundary queries and a command-triggered completion.</returns>
    [Fact]
    public async Task ExactEndChangesEffectiveReadAndCommandCompletesOnce()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var end = TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End;
        var sut = context.Service(seed.User);
        context.Clock.Now = end.AddTicks(-1);
        Assert.Equal(EventStatus.Active, (await sut.GetAsync(seed.Event.Id)).Summary.Status);
        context.Clock.Now = end;
        Assert.Equal(EventStatus.Completed, (await sut.GetAsync(seed.Event.Id)).Summary.Status);
        await using (var beforeCommand = database.CreateContext())
            Assert.Equal(EventStatus.Active, (await beforeCommand.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        for (var repeat = 0; repeat < 2; repeat++)
            Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
                sut.RequestMembershipAsync(seed.Event.Id))).Code);
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Completed, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Completed, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
        var transition = await read.EventStatusHistory.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(EventStatus.Active, transition.Previous);
        Assert.Equal(EventStatus.Completed, transition.Next);
        Assert.Equal(end, transition.OccurredUtc);
        Assert.Equal("Complete", Assert.Single(context.Quests.Calls).Action);
    }

    /// <summary>Publishes a draft once with a scheduled end, rejects deleting published aggregates, and deletes only a clean unpublished draft.</summary>
    /// <returns>A task completing after publish, guarded delete and permitted delete persistence checks.</returns>
    [Fact]
    public async Task PublishSchedulesCompletionAndOnlyCleanDraftCanBeDeleted()
    {
        var context = new EventTestContext(database);
        var user = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, user);
        var sut = context.Service(user);
        var published = await sut.CreateAsync(EventTestContext.Input());
        var version = (await sut.GetAsync(published)).Summary.Version;
        await sut.ChangeStatusAsync(published, version, EventStatus.Active, "");
        var active = await sut.GetAsync(published);
        Assert.Equal(EventStatus.Active, active.Summary.Status);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            sut.DeleteDraftAsync(published, active.Summary.Version))).Code);
        var clean = await sut.CreateAsync(EventTestContext.Input("Clean draft"));
        await sut.DeleteDraftAsync(clean, (await sut.GetAsync(clean)).Summary.Version);
        await using var read = database.CreateContext();
        Assert.True(await read.Events.AnyAsync(x => x.Id == published));
        Assert.False(await read.Events.AnyAsync(x => x.Id == clean));
        Assert.Empty(await read.EventOwners.Where(x => x.EventId == clean).ToListAsync());
        Assert.Empty(await read.EventMemberships.Where(x => x.EventId == clean).ToListAsync());
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == clean && x.Action == "Event.Deleted").ToListAsync());
        Assert.Single(await read.ScheduledWork.Where(x => x.PayloadJson.Contains(published.ToString())).ToListAsync());
    }

    /// <summary>Rejects archiving a completed Event with a live child and accepts it once that child is terminal.</summary>
    /// <returns>A task completing after both sides of the archive guard.</returns>
    [Fact]
    public async Task ArchiveRequiresAllChildrenTerminal()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        await using (var setup = database.CreateContext())
        {
            (await setup.Events.FindAsync(seed.Event.Id))!.Status = EventStatus.Completed;
            await setup.SaveChangesAsync();
        }
        var sut = context.Service(seed.User);
        var version = (await sut.GetAsync(seed.Event.Id)).Summary.Version;
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            sut.ChangeStatusAsync(seed.Event.Id, version, EventStatus.Archived, ""))).Code);
        await using (var setup = database.CreateContext())
        {
            (await setup.Quests.FindAsync(seed.Quest.Id))!.Status = QuestStatus.Cancelled;
            await setup.SaveChangesAsync();
        }
        await sut.ChangeStatusAsync(seed.Event.Id, version, EventStatus.Archived, "");
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Archived, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(EventStatus.Archived, (await read.EventStatusHistory.SingleAsync(x => x.EventId == seed.Event.Id)).Next);
    }

    /// <summary>Cancels parent and child in one transaction, resolves pending consent and records one parent history transition.</summary>
    /// <returns>A task completing after concrete cancellation and audit observations.</returns>
    [Fact]
    public async Task CancellationCommitsChildAndClosesPendingConsent()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        await FoundationSeed.PersistAsync(database,
            new EventMembershipRequest { EventId = seed.Event.Id, UserId = seed.Other.Id, CreatedUtc = FoundationSeed.Now },
            new EventInvitation { EventId = seed.Event.Id, UserId = seed.Other.Id, InvitedById = seed.User.Id,
                CreatedUtc = FoundationSeed.Now, ExpiresUtc = FoundationSeed.Now.AddDays(1) });
        await context.Service(seed.User).ChangeStatusAsync(seed.Event.Id,
            Convert.ToBase64String(seed.Event.Version), EventStatus.Cancelled, "Venue unavailable");
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Cancelled, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Cancelled, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
        var history = await read.EventStatusHistory.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(EventStatus.Active, history.Previous);
        Assert.Equal(EventStatus.Cancelled, history.Next);
        Assert.Equal("Venue unavailable", history.Reason);
        Assert.Equal(seed.User.Id, history.ActorId);
        var request = await read.MembershipRequests.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(MembershipRequestStatus.Rejected, request.Status);
        Assert.Equal("The Event was cancelled and is no longer accepting membership requests.", request.Reason);
        Assert.Null(request.DecidedById);
        Assert.Equal(EventInvitationStatus.Expired, (await read.EventInvitations.SingleAsync(x => x.EventId == seed.Event.Id)).Status);
        Assert.Equal("Cancel", Assert.Single(context.Quests.Calls).Action);
        Assert.Single(await read.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync());
        var requesterView = Assert.Single((await context.Service(seed.Other).ListRequestsAsync(null, new())).Items);
        Assert.Equal("Unavailable Event", requesterView.EventName);
        Assert.DoesNotContain("Venue unavailable", requesterView.Reason);
    }

    /// <summary>Retains unpublished Drafts that already have any request, invitation, child Quest or lifecycle history.</summary>
    /// <param name="dependency">Persisted relation independently preventing deletion.</param>
    /// <returns>A task completing after the guarded command and unchanged aggregate/owner assertions.</returns>
    [Theory]
    [InlineData("request")]
    [InlineData("invitation")]
    [InlineData("quest")]
    [InlineData("history")]
    public async Task DeleteDraftRejectsEachRetainedDependency(string dependency)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var sut = context.Service(seed.User);
        var id = await sut.CreateAsync(EventTestContext.Input("Guarded draft"));
        Entity row = dependency switch
        {
            "request" => new EventMembershipRequest { EventId = id, UserId = seed.Other.Id, CreatedUtc = FoundationSeed.Now },
            "invitation" => new EventInvitation { EventId = id, UserId = seed.Other.Id, InvitedById = seed.User.Id,
                CreatedUtc = FoundationSeed.Now, ExpiresUtc = FoundationSeed.Now.AddDays(1) },
            "quest" => FoundationSeed.NewQuest(id, seed.User.Id),
            _ => new EventStatusHistory { EventId = id, Previous = EventStatus.Draft, Next = EventStatus.Active,
                ActorId = seed.User.Id, Reason = "Retained synthetic history", OccurredUtc = FoundationSeed.Now }
        };
        await FoundationSeed.PersistAsync(database, row);
        var version = (await sut.GetAsync(id)).Summary.Version;
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => sut.DeleteDraftAsync(id, version))).Code);
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Draft, (await read.Events.SingleAsync(x => x.Id == id)).Status);
        Assert.Equal(seed.User.Id, (await read.EventOwners.SingleAsync(x => x.EventId == id)).UserId);
        Assert.Empty(await read.AuditEntries.Where(x => x.ResourceId == id && x.Action == "Event.Deleted").ToListAsync());
    }

    /// <summary>Rejects invalid page inputs through the actual list API rather than testing the paging DTO alone.</summary>
    /// <param name="page">One-based page input.</param>
    /// <param name="size">Requested page capacity.</param>
    /// <returns>A task completing after validation and no-side-effect assertions.</returns>
    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task ListRejectsInvalidPaging(int page, int size)
    {
        var context = new EventTestContext(database);
        var user = FoundationSeed.NewUser();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            context.Service(user).ListAsync(EventListKind.Available, new(page, size)));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Empty(context.Quests.Calls);
        Assert.Equal(0, context.Directory.UserCalls);
    }
}
