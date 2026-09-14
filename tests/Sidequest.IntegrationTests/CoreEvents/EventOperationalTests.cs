using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Events;
using Sidequest.Application.Events.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Exercises operational projections, tenant boundaries, draft membership authority, and configured limits in real SQL.</summary>
/// <param name="database">One uniquely owned migrated SQL fixture for the sequential class.</param>
public sealed class EventOperationalTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Counts only affected child states for an owner without exposing child details or granting nonmember access.</summary>
    /// <returns>A task completing after exact count, denied access, and lifecycle guard assertions.</returns>
    [Fact]
    public async Task CancellationImpactCountsOnlyAffectedStatesAndRequiresOwnership()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        foreach (var status in new[] { QuestStatus.Draft, QuestStatus.Suspended, QuestStatus.Completed, QuestStatus.Cancelled, QuestStatus.Archived })
        {
            var quest = FoundationSeed.NewQuest(seed.Event.Id, seed.User.Id);
            quest.Status = status;
            await FoundationSeed.PersistAsync(database, quest);
        }
        Assert.Equal(3, await context.Service(seed.User).GetCancellationImpactAsync(seed.Event.Id));
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            context.Service(seed.Other).GetCancellationImpactAsync(seed.Event.Id))).Code);
        await using var db = database.CreateContext();
        (await db.Events.FindAsync(seed.Event.Id))!.Status = EventStatus.Completed;
        await db.SaveChangesAsync();
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            context.Service(seed.User).GetCancellationImpactAsync(seed.Event.Id))).Code);
        Assert.Empty(await db.AuditEntries.Where(x => x.ResourceId == seed.Event.Id).ToListAsync());
    }

    /// <summary>A remaining Draft owner relation never overrides a removed individual membership on any Event read or edit.</summary>
    /// <returns>A task completing after discovery, management, mutation, and unchanged-state checks.</returns>
    [Fact]
    public async Task DraftOwnerWithoutActiveMembershipHasNoAccess()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var id = await context.Service(seed.User).CreateAsync(EventTestContext.Input());
        string version;
        await using (var setup = database.CreateContext())
        {
            version = Convert.ToBase64String((await setup.Events.FindAsync(id))!.Version);
            var membership = await setup.EventMemberships.SingleAsync(x => x.EventId == id);
            membership.Status = MembershipStatus.Removed;
            await setup.SaveChangesAsync();
        }
        var sut = context.Service(seed.User);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.GetAsync(id))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.ListMembersAsync(id, new()))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.ListRequestsAsync(id, new()))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.ListInvitationsAsync(id, new()))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.EditAsync(id, version, EventTestContext.Input("Unauthorized edit")))).Code);
        Assert.DoesNotContain((await sut.ListAsync(EventListKind.Mine, new(1, 100))).Items, x => x.Id == id);
        await using var read = database.CreateContext();
        Assert.Equal("New Event", (await read.Events.FindAsync(id))!.Name);
        Assert.Single(await read.EventOwners.Where(x => x.EventId == id).ToListAsync());
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == id).ToListAsync());
    }

    /// <summary>An eligible account from a different tenant cannot discover, request, or inspect another tenant's Event.</summary>
    /// <returns>A task completing after zero-disclosure queries and no pending-request side effects.</returns>
    [Fact]
    public async Task TenantBoundaryExcludesDiscoveryDuplicatesAndRequests()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var otherTenant = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, otherTenant);
        var sut = context.Service(otherTenant);
        Assert.Empty((await sut.ListAsync(EventListKind.Available, new())).Items);
        Assert.Empty(await sut.FindDuplicatesAsync(seed.Event.Name, seed.Event.StartDate, seed.Event.EndDate));
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.GetAsync(seed.Event.Id))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => sut.RequestMembershipAsync(seed.Event.Id))).Code);
        await using var read = database.CreateContext();
        Assert.False(await read.MembershipRequests.AnyAsync(x => x.UserId == otherTenant.Id));
    }

    /// <summary>Draft audience configuration and removal remain audited but send no invitations or audience notification work.</summary>
    /// <returns>A task completing after explicit add, remove, restore, and preserved-history assertions.</returns>
    [Fact]
    public async Task DraftAudienceChangesNeverNotifyBeforePublication()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var sut = context.Service(seed.User);
        var id = await sut.CreateAsync(EventTestContext.Input());
        await sut.AddMemberAsync(id, seed.Other.ObjectId, false);
        await sut.RemoveMemberAsync(id, seed.Other.Id, "Draft audience changed.");
        await sut.AddMemberAsync(id, seed.Other.ObjectId, true);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => sut.InviteAsync(id, seed.Other.ObjectId))).Code);
        await using var read = database.CreateContext();
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync());
        Assert.Empty(await read.EventInvitations.Where(x => x.EventId == id).ToListAsync());
        Assert.Equal(2, await read.AuditEntries.CountAsync(x => x.ResourceId == id && x.Action == "Membership.Activated"));
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == id && x.Action == "Membership.Removed").ToListAsync());
    }

    /// <summary>Retained withdrawn requests count toward the configured rate limit until the exact one-hour window expires.</summary>
    /// <returns>A task completing after idempotent repeats, limit rejection, and a later successful request.</returns>
    [Fact]
    public async Task RequestRateLimitCountsHistoryAndResetsAfterWindow()
    {
        var context = new EventTestContext(database) { Options = new() { RequestsPerHour = 1 } };
        var seed = await context.SeedAsync();
        var sut = context.Service(seed.Other);
        await sut.RequestMembershipAsync(seed.Event.Id);
        await sut.RequestMembershipAsync(seed.Event.Id);
        var request = Assert.Single((await sut.ListRequestsAsync(null, new())).Items);
        await sut.WithdrawRequestAsync(request.Id);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => sut.RequestMembershipAsync(seed.Event.Id))).Code);
        context.Clock.Now = FoundationSeed.Now.AddHours(1).AddTicks(1);
        await sut.RequestMembershipAsync(seed.Event.Id);
        await using var read = database.CreateContext();
        Assert.Equal(2, await read.MembershipRequests.CountAsync(x => x.EventId == seed.Event.Id));
        Assert.Single(await read.MembershipRequests.Where(x => x.EventId == seed.Event.Id && x.Status == MembershipRequestStatus.Pending).ToListAsync());
    }

    /// <summary>Rejects excess queued bulk starts atomically and reports frozen recipients through owner-only deterministic paging.</summary>
    /// <returns>A task completing after rate-limit, recipient status, pagination, and denied-read assertions.</returns>
    [Fact]
    public async Task BulkLimitsAndOutcomePagingRemainOwnerScoped()
    {
        var context = new EventTestContext(database) { Options = new() { BulkStartsPerHour = 1 } };
        var seed = await context.SeedAsync();
        var sut = context.Service(seed.User);
        var operation = await sut.StartBulkAsync(seed.Event.Id, Guid.NewGuid(), BulkMode.Invite);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            sut.StartBulkAsync(seed.Event.Id, Guid.NewGuid(), BulkMode.Add))).Code);
        await FoundationSeed.PersistAsync(database,
            new BulkMembershipRecipient { OperationId = operation, UserId = seed.User.Id, Status = BulkRecipientStatus.Skipped, Detail = "Already a member." },
            new BulkMembershipRecipient { OperationId = operation, UserId = seed.Other.Id, Status = BulkRecipientStatus.Failed, Detail = "No longer eligible." });
        var first = await sut.ListBulkRecipientsAsync(operation, new(1, 1));
        var second = await sut.ListBulkRecipientsAsync(operation, new(2, 1));
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(2, second.TotalCount);
        var rows = first.Items.Concat(second.Items).ToArray();
        Assert.Equal(2, rows.Select(x => x.User.Id).Distinct().Count());
        Assert.Contains(rows, x => x.User.Id == seed.Other.Id && x.Status == BulkRecipientStatus.Failed && x.Detail == "No longer eligible.");
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            context.Service(seed.Other).ListBulkRecipientsAsync(operation, new()))).Code);
        await using var read = database.CreateContext();
        Assert.Single(await read.BulkOperations.Where(x => x.EventId == seed.Event.Id).ToListAsync());
    }

    /// <summary>The SQL lock adapter rejects calls without a transaction rather than pretending pessimistic serialization succeeded.</summary>
    /// <returns>A task completing after explicit provider-contract denial.</returns>
    [Fact]
    public async Task SqlLockRequiresCallerOwnedTransaction()
    {
        await using var db = database.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LockEventAsync(Guid.NewGuid()));
    }

    /// <summary>Duplicate normalization replaces punctuation and whitespace but preserves meaningful symbols such as C++.</summary>
    /// <returns>A task completing after symbol-distinct and punctuation-equivalent matching assertions.</returns>
    [Fact]
    public async Task DuplicateNormalizationPreservesNonPunctuationSymbols()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        await using (var setup = database.CreateContext())
        {
            (await setup.Events.FindAsync(seed.Event.Id))!.Name = "C++ Evening";
            await setup.SaveChangesAsync();
        }
        var sut = context.Service(seed.User);
        Assert.Empty(await sut.FindDuplicatesAsync("C Evening", seed.Event.StartDate, seed.Event.EndDate));
        var match = Assert.Single(await sut.FindDuplicatesAsync("c++ :  evening", seed.Event.StartDate, seed.Event.EndDate));
        Assert.Equal(seed.Event.Id, match.Event.Id);
        Assert.Equal(1, match.Similarity);
    }

    /// <summary>A direct manager add resolves both pending consent records once and records the manager and direct-add reason against the invitation.</summary>
    /// <returns>A task completing after retained consent, membership, resolution audit, and idempotency assertions.</returns>
    [Fact]
    public async Task DirectAddResolvesConsentAndRecordsManager()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var owner = context.Service(seed.User);
        await context.Service(seed.Other).RequestMembershipAsync(seed.Event.Id);
        await owner.InviteAsync(seed.Event.Id, seed.Other.ObjectId);
        await owner.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false);
        await owner.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false);
        await using var read = database.CreateContext();
        var request = await read.MembershipRequests.SingleAsync(x => x.EventId == seed.Event.Id);
        var invitation = await read.EventInvitations.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(MembershipRequestStatus.Approved, request.Status);
        Assert.Equal(seed.User.Id, request.DecidedById);
        Assert.Equal("An Event owner explicitly added this individual.", request.Reason);
        Assert.Equal(EventInvitationStatus.Accepted, invitation.Status);
        Assert.Equal(FoundationSeed.Now, invitation.ResolvedUtc);
        var decision = await read.AuditEntries.SingleAsync(x => x.ResourceId == seed.Event.Id && x.Action == "Invitation.Accepted");
        Assert.Equal(seed.User.Id, decision.ActorId);
        Assert.Contains(invitation.Id.ToString("N"), decision.Reason);
        Assert.Contains("An Event owner explicitly added this individual.", decision.Reason);
        Assert.Single(await read.EventMemberships.Where(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id &&
            x.Status == MembershipStatus.Active).ToListAsync());
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Membership.Activated").ToListAsync());
        Assert.Empty(context.Quests.Calls);
    }

    /// <summary>Cancelling and archiving an unpublished Draft never exposes its content or existence to configured nonowner members.</summary>
    /// <returns>A task completing after owner history remains visible and nonowner detail, roster, discovery, and history are denied.</returns>
    [Fact]
    public async Task CancelledUnpublishedDraftRemainsOwnerOnlyThroughArchive()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var owner = context.Service(seed.User);
        var member = context.Service(seed.Other);
        var id = await owner.CreateAsync(EventTestContext.Input("Unpublished confidential Event"));
        await owner.AddMemberAsync(id, seed.Other.ObjectId, false);
        var draft = await owner.GetAsync(id);
        await owner.ChangeStatusAsync(id, draft.Summary.Version, EventStatus.Cancelled, "Unpublished planning was cancelled.");
        foreach (var state in new[] { EventStatus.Cancelled, EventStatus.Archived })
        {
            if (state == EventStatus.Archived)
            {
                var cancelled = await owner.GetAsync(id);
                await owner.ChangeStatusAsync(id, cancelled.Summary.Version, EventStatus.Archived, "");
            }
            Assert.Equal(state, (await owner.GetAsync(id)).Summary.Status);
            Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => member.GetAsync(id))).Code);
            Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => member.ListMembersAsync(id, new()))).Code);
            Assert.DoesNotContain((await member.ListAsync(EventListKind.History, new(1, 100))).Items, x => x.Id == id);
            Assert.DoesNotContain((await member.ListAsync(EventListKind.Available, new(1, 100))).Items, x => x.Id == id);
        }
        await using var read = database.CreateContext();
        Assert.True(await read.EventMemberships.AnyAsync(x => x.EventId == id && x.UserId == seed.Other.Id &&
            x.Status == MembershipStatus.Active));
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync());
        Assert.Equal(2, await read.EventStatusHistory.CountAsync(x => x.EventId == id));
        await member.LeaveAsync(id);
        Assert.True(await read.EventMemberships.AnyAsync(x => x.EventId == id && x.UserId == seed.Other.Id &&
            x.Status == MembershipStatus.Removed));
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync());
    }

    /// <summary>Invalid text and date-zone partitions fail before creating any aggregate, audit, owner, or scheduled work.</summary>
    /// <param name="partition">Independent server-validation boundary to exercise.</param>
    /// <returns>A task completing after the expected field and no-write assertions.</returns>
    [Theory]
    [InlineData("name-short")]
    [InlineData("name-long")]
    [InlineData("summary-long")]
    [InlineData("description-long")]
    [InlineData("reverse-dates")]
    [InlineData("invalid-zone")]
    public async Task CreateValidatesServerInputBeforePersistence(string partition)
    {
        var context = new EventTestContext(database);
        var user = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, user);
        var input = partition switch
        {
            "name-short" => EventTestContext.Input("ab"),
            "name-long" => EventTestContext.Input(new string('n', 121)),
            "summary-long" => EventTestContext.Input() with { DiscoverySummary = new string('s', 301) },
            "description-long" => EventTestContext.Input() with { Description = new string('d', 10001) },
            "reverse-dates" => EventTestContext.Input() with { EndDate = new(2026, 7, 14) },
            _ => EventTestContext.Input() with { TimeZoneId = "Unknown/Zone" }
        };
        var expectedField = partition switch
        {
            "name-short" or "name-long" => "Name",
            "summary-long" => "DiscoverySummary",
            "description-long" => "Description",
            "reverse-dates" => "EndDate",
            _ => "TimeZoneId"
        };
        var error = await Assert.ThrowsAsync<DomainException>(() => context.Service(user).CreateAsync(input));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal(expectedField, error.Field);
        await using var read = database.CreateContext();
        Assert.False(await read.Events.AnyAsync(x => x.CreatorId == user.Id));
        Assert.False(await read.AuditEntries.AnyAsync(x => x.ActorId == user.Id));
        Assert.False(await read.EventOwners.AnyAsync(x => x.UserId == user.Id));
    }
}
