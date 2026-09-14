using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Tests consent, retained history, individual access loss and equal-owner continuity on real SQL transactions.</summary>
/// <param name="database">The uniquely owned migrated SQL fixture for this class.</param>
public sealed class EventMembershipTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Captures membership action targets independently of managers and actors for every Event notification producer.</summary>
    /// <param name="action">The direct, consent, access-loss, owner-admission, or multi-member publication workflow.</param>
    /// <returns>A task completing after persisted delivery kind, actor, target, and observer assertions.</returns>
    [Theory]
    [InlineData("add")]
    [InlineData("restore")]
    [InlineData("owner")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("revoke")]
    [InlineData("remove")]
    [InlineData("leave")]
    [InlineData("publish")]
    public async Task DeliveryTargetsAreCapturedSeparatelyFromManagers(string action)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var manager = context.Service(seed.User);
        var recipient = context.Service(seed.Other);
        var eventId = seed.Event.Id;
        Guid[] targets = [seed.Other.Id];
        var kind = NotificationKind.MembershipAdded;
        var actor = seed.User.Id;
        var observes = action is "add" or "restore" or "owner" or "approve" or "accept" or "decline";
        switch (action)
        {
            case "restore":
                await FoundationSeed.PersistAsync(database, seed.Membership(seed.Other.Id, MembershipStatus.Removed));
                await manager.AddMemberAsync(eventId, seed.Other.ObjectId, true);
                break;
            case "add":
                await manager.AddMemberAsync(eventId, seed.Other.ObjectId, false);
                break;
            case "owner":
                await manager.AddOwnerAsync(eventId, seed.Other.ObjectId);
                break;
            case "approve" or "reject":
                await recipient.RequestMembershipAsync(eventId);
                var request = Assert.Single((await manager.ListRequestsAsync(eventId, new())).Items);
                await manager.DecideRequestAsync(request.Id, action == "approve", "A deliberate membership decision.");
                kind = NotificationKind.MembershipDecided;
                break;
            case "accept" or "decline" or "revoke":
                await manager.InviteAsync(eventId, seed.Other.ObjectId);
                var invitation = Assert.Single((await recipient.ListInvitationsAsync(null, new())).Items);
                if (action == "revoke")
                    await manager.RevokeInvitationAsync(invitation.Id);
                else
                {
                    await recipient.RespondToInvitationAsync(invitation.Id, action == "accept");
                    actor = seed.Other.Id;
                }
                kind = NotificationKind.MembershipDecided;
                break;
            case "remove" or "leave":
                await FoundationSeed.PersistAsync(database, seed.Membership(seed.Other.Id));
                if (action == "remove")
                    await manager.RemoveMemberAsync(eventId, seed.Other.Id, "Membership deliberately removed.");
                else
                {
                    await recipient.LeaveAsync(eventId);
                    actor = seed.Other.Id;
                }
                kind = NotificationKind.AccessRemoved;
                break;
            case "publish":
                eventId = await manager.CreateAsync(EventTestContext.Input());
                var second = FoundationSeed.NewUser();
                second.TenantId = seed.User.TenantId;
                await FoundationSeed.PersistAsync(database, second,
                    new EventMembership { EventId = eventId, UserId = second.Id, ChangedById = seed.User.Id, ChangedUtc = FoundationSeed.Now });
                await manager.AddMemberAsync(eventId, seed.Other.ObjectId, false);
                await manager.ChangeStatusAsync(eventId, (await manager.GetAsync(eventId)).Summary.Version, EventStatus.Active, "");
                targets = [seed.Other.Id, second.Id];
                break;
        }
        await using var read = database.CreateContext();
        var messages = await read.OutboxMessages.Where(x => x.AggregateId == eventId).ToListAsync();
        var envelope = Assert.Single(messages.Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!),
            x => x.Kind == kind);
        Assert.Equal(actor, envelope.ActorId);
        var affected = Assert.IsType<Guid[]>(envelope.AffectedUserIds);
        Assert.Equal(targets.Order(), affected.Order());
        Assert.DoesNotContain(seed.User.Id, affected);
        var recipients = observes ? targets.Append(seed.User.Id) : targets;
        Assert.Equal(recipients.Order(), envelope.RecipientIds.Order());
        Assert.Equal(FoundationSeed.Now, envelope.OccurredUtc);
    }

    /// <summary>Retains withdrawn and rejected requests, deduplicates retries and grants membership only after owner approval.</summary>
    /// <returns>A task completing after requester and manager history plus membership/delivery assertions.</returns>
    [Fact]
    public async Task RequestWithdrawRejectAndApproveRetainHistoryAndAreIdempotent()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var requester = context.Service(seed.Other);
        var manager = context.Service(seed.User);
        await requester.RequestMembershipAsync(seed.Event.Id);
        await requester.RequestMembershipAsync(seed.Event.Id);
        var initial = Assert.Single((await requester.ListRequestsAsync(null, new())).Items);
        await using (var read = database.CreateContext())
        {
            Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
            Assert.Single(await read.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync());
        }
        await requester.WithdrawRequestAsync(initial.Id);
        await requester.WithdrawRequestAsync(initial.Id);
        await requester.RequestMembershipAsync(seed.Event.Id);
        var pending = (await manager.ListRequestsAsync(seed.Event.Id, new())).Items.Single(x => x.Status == MembershipRequestStatus.Pending);
        var missingReason = await Assert.ThrowsAsync<DomainException>(() => manager.DecideRequestAsync(pending.Id, false, " "));
        Assert.Equal(ErrorCode.Validation, missingReason.Code);
        await manager.DecideRequestAsync(pending.Id, false, "Capacity is reserved");
        await manager.DecideRequestAsync(pending.Id, false, "Capacity is reserved");
        await requester.RequestMembershipAsync(seed.Event.Id);
        var approval = (await manager.ListRequestsAsync(seed.Event.Id, new())).Items.Single(x => x.Status == MembershipRequestStatus.Pending);
        await manager.DecideRequestAsync(approval.Id, true, "");
        await manager.DecideRequestAsync(approval.Id, true, "");
        await requester.RequestMembershipAsync(seed.Event.Id);
        await using var final = database.CreateContext();
        var rows = await final.MembershipRequests.Where(x => x.EventId == seed.Event.Id).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(MembershipRequestStatus.Withdrawn, rows.Single(x => x.Id == initial.Id).Status);
        Assert.Equal("Capacity is reserved", rows.Single(x => x.Id == pending.Id).Reason);
        var accepted = rows.Single(x => x.Id == approval.Id);
        Assert.Equal(MembershipRequestStatus.Approved, accepted.Status);
        Assert.Equal(seed.User.Id, accepted.DecidedById);
        Assert.Equal(FoundationSeed.Now, accepted.DecidedUtc);
        var member = await final.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id);
        Assert.Equal(MembershipStatus.Active, member.Status);
        Assert.Equal(seed.User.Id, member.ChangedById);
        Assert.Equal(3, (await requester.ListRequestsAsync(null, new())).TotalCount);
        Assert.Empty(context.Quests.Calls);
    }

    /// <summary>Named invitations grant no membership before consent and accepting a repeated invitation resolves pending requests exactly once.</summary>
    /// <returns>A task completing after invitation, membership and retained request checks.</returns>
    [Fact]
    public async Task InviteAndAcceptAreRepeatSafeAndResolvePendingRequest()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var owner = context.Service(seed.User);
        var recipient = context.Service(seed.Other);
        await recipient.RequestMembershipAsync(seed.Event.Id);
        await owner.InviteAsync(seed.Event.Id, seed.Other.ObjectId);
        await owner.InviteAsync(seed.Event.Id, seed.Other.ObjectId);
        var invitation = Assert.Single((await recipient.ListInvitationsAsync(null, new())).Items);
        await using (var read = database.CreateContext())
            Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        await recipient.RespondToInvitationAsync(invitation.Id, true);
        await recipient.RespondToInvitationAsync(invitation.Id, true);
        await owner.InviteAsync(seed.Event.Id, seed.Other.ObjectId);
        await using var final = database.CreateContext();
        var saved = await final.EventInvitations.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(EventInvitationStatus.Accepted, saved.Status);
        Assert.Equal(FoundationSeed.Now, saved.ResolvedUtc);
        Assert.Equal(TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End, saved.ExpiresUtc);
        Assert.Equal(MembershipStatus.Active, (await final.EventMemberships.SingleAsync(x =>
            x.EventId == seed.Event.Id && x.UserId == seed.Other.Id)).Status);
        Assert.Equal(MembershipRequestStatus.Approved, (await final.MembershipRequests.SingleAsync(x => x.EventId == seed.Event.Id)).Status);
        Assert.Single(await final.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Membership.Activated").ToListAsync());
        Assert.Empty(context.Quests.Calls);
    }

    /// <summary>Declining or revoking is idempotent and terminal; neither path grants membership or permits a later acceptance.</summary>
    /// <param name="revoke">Whether the owner revokes instead of the recipient declining.</param>
    /// <returns>A task completing after persisted terminal state and denied acceptance checks.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclineOrRevokeRetainsHistoryWithoutMembership(bool revoke)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var owner = context.Service(seed.User);
        var recipient = context.Service(seed.Other);
        await owner.InviteAsync(seed.Event.Id, seed.Other.ObjectId);
        var invitation = Assert.Single((await recipient.ListInvitationsAsync(null, new())).Items);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            if (revoke)
                await owner.RevokeInvitationAsync(invitation.Id);
            else
                await recipient.RespondToInvitationAsync(invitation.Id, false);
        }
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            recipient.RespondToInvitationAsync(invitation.Id, true))).Code);
        await using var read = database.CreateContext();
        var saved = await read.EventInvitations.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(revoke ? EventInvitationStatus.Revoked : EventInvitationStatus.Declined, saved.Status);
        Assert.Equal(FoundationSeed.Now, saved.ResolvedUtc);
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.Equal(2, await read.OutboxMessages.CountAsync(x => x.AggregateId == seed.Event.Id));
    }

    /// <summary>Applies the invitation deadline at the exact instant, not one tick early, without needing Event completion.</summary>
    /// <param name="offsetTicks">Offset from the controlled invitation expiry.</param>
    /// <param name="accepted">Expected acceptance decision.</param>
    /// <returns>A task completing after query projection and command checks.</returns>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task InvitationExpiryUsesExactBoundary(long offsetTicks, bool accepted)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var expires = FoundationSeed.Now.AddHours(1);
        var invitation = new EventInvitation
        {
            EventId = seed.Event.Id, UserId = seed.Other.Id, InvitedById = seed.User.Id,
            CreatedUtc = FoundationSeed.Now, ExpiresUtc = expires
        };
        await FoundationSeed.PersistAsync(database, invitation);
        context.Clock.Now = expires.AddTicks(offsetTicks);
        var sut = context.Service(seed.Other);
        var summary = Assert.Single((await sut.ListInvitationsAsync(null, new())).Items);
        Assert.Equal(accepted ? EventInvitationStatus.Pending : EventInvitationStatus.Expired, summary.Status);
        if (accepted)
            await sut.RespondToInvitationAsync(invitation.Id, true);
        else
            Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => sut.RespondToInvitationAsync(invitation.Id, true))).Code);
        await using var read = database.CreateContext();
        Assert.Equal(accepted, await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.Equal(accepted ? EventInvitationStatus.Accepted : EventInvitationStatus.Expired,
            (await read.EventInvitations.SingleAsync(x => x.Id == invitation.Id)).Status);
        if (!accepted)
            Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Invitation.Expired").ToListAsync());
    }

    /// <summary>Direct add is idempotent, removal calls child cleanup once, and explicit restore does not restore child participation.</summary>
    /// <returns>A task completing after all persisted membership transitions and collaborator calls.</returns>
    [Fact]
    public async Task AddRemoveAndExplicitRestorePreserveIndividualConsent()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var sut = context.Service(seed.User);
        await sut.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false);
        await sut.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false);
        await sut.RemoveMemberAsync(seed.Event.Id, seed.Other.Id, "Owner removed access");
        await sut.RemoveMemberAsync(seed.Event.Id, seed.Other.Id, "Owner removed access");
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            sut.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false))).Code);
        await sut.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, true);
        var call = Assert.Single(context.Quests.Calls);
        Assert.Equal(("Remove", seed.Event.Id, (Guid?)seed.Other.Id, (Guid?)seed.User.Id, "Owner removed access", FoundationSeed.Now), call);
        await using var read = database.CreateContext();
        var membership = await read.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Equal(seed.User.Id, membership.ChangedById);
        Assert.Equal(2, await read.AuditEntries.CountAsync(x => x.ResourceId == seed.Event.Id && x.Action == "Membership.Activated"));
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Membership.Removed").ToListAsync());
        Assert.Equal(QuestStatus.Active, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
    }

    /// <summary>Ordinary members may leave once; Event and child ownership independently block membership loss until assignments are removed.</summary>
    /// <returns>A task completing after guarded owner paths and repeat-safe member leaving.</returns>
    [Fact]
    public async Task LeaveAndRemoveHonorEventAndChildOwnershipGuards()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        await FoundationSeed.PersistAsync(database, seed.Membership(seed.Other.Id),
            new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.Other.Id });
        var owner = context.Service(seed.User);
        var member = context.Service(seed.Other);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => owner.LeaveAsync(seed.Event.Id))).Code);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            owner.RemoveMemberAsync(seed.Event.Id, seed.Other.Id, "Remove this member"))).Code);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => member.LeaveAsync(seed.Event.Id))).Code);
        Assert.Empty(context.Quests.Calls);
        await using (var setup = database.CreateContext())
        {
            setup.QuestOwners.Remove(await setup.QuestOwners.SingleAsync(x => x.QuestId == seed.Quest.Id));
            await setup.SaveChangesAsync();
        }
        await member.LeaveAsync(seed.Event.Id);
        await member.LeaveAsync(seed.Event.Id);
        var call = Assert.Single(context.Quests.Calls);
        Assert.Equal(seed.Other.Id, call.User);
        Assert.Equal(seed.Other.Id, call.Actor);
        await using var read = database.CreateContext();
        Assert.Equal(MembershipStatus.Removed, (await read.EventMemberships.SingleAsync(x =>
            x.EventId == seed.Event.Id && x.UserId == seed.Other.Id)).Status);
        Assert.Equal(MembershipStatus.Active, (await read.EventMemberships.SingleAsync(x =>
            x.EventId == seed.Event.Id && x.UserId == seed.User.Id)).Status);
    }

    /// <summary>Rejects untrusted directory identity partitions without changing memberships, owner assignments, or delivery intent.</summary>
    /// <param name="partition">Invalid ID, mismatched identity, cross-tenant, provider-ineligible, local-ineligible or departed target.</param>
    /// <returns>A task completing after safe failure and no-side-effect checks.</returns>
    [Theory]
    [InlineData("empty")]
    [InlineData("mismatch")]
    [InlineData("tenant")]
    [InlineData("directory-ineligible")]
    [InlineData("local-ineligible")]
    [InlineData("departed")]
    public async Task AddRejectsInvalidDirectoryAndLocallyRevokedTargets(string partition)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var identity = context.Directory.Users[seed.Other.ObjectId];
        context.Directory.Users[seed.Other.ObjectId] = partition switch
        {
            "mismatch" => identity with { ObjectId = Guid.NewGuid() },
            "tenant" => identity with { TenantId = Guid.NewGuid() },
            "directory-ineligible" => identity with { IsEligible = false },
            _ => identity
        };
        if (partition is "local-ineligible" or "departed")
        {
            await using var setup = database.CreateContext();
            var user = (await setup.Users.FindAsync(seed.Other.Id))!;
            user.IsEligible = partition != "local-ineligible";
            user.DepartureVerifiedUtc = partition == "departed" ? FoundationSeed.Now : null;
            await setup.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<DomainException>(() => context.Service(seed.User).AddMemberAsync(
            seed.Event.Id, partition == "empty" ? Guid.Empty : seed.Other.ObjectId, false));
        Assert.Equal(partition == "mismatch" ? ErrorCode.DependencyUnavailable : ErrorCode.Validation, error.Code);
        Assert.Equal(partition == "empty" ? 0 : 1, context.Directory.UserCalls);
        await using var read = database.CreateContext();
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.Equal(seed.User.Id, (await read.EventOwners.SingleAsync(x => x.EventId == seed.Event.Id)).UserId);
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync());
    }

    /// <summary>Starts competing equal-owner self-removals together and proves SQL serialization preserves one eligible member-owner.</summary>
    /// <returns>A task completing after both racing outcomes and the persisted ownership invariant are checked.</returns>
    [Fact]
    public async Task RacingEqualOwnerRemovalsRetainLastEligibleOwner()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        await context.Service(seed.User).AddOwnerAsync(seed.Event.Id, seed.Other.ObjectId);
        // Separate service instances and SQL contexts; release both commands from the same explicit gate.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> RemoveAsync(UserAccount actor)
        {
            await gate.Task;
            return await Record.ExceptionAsync(() => context.Service(actor).RemoveOwnerAsync(seed.Event.Id, actor.Id));
        }
        var first = RemoveAsync(seed.User);
        var second = RemoveAsync(seed.Other);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, x => x is null);
        Assert.Equal(ErrorCode.Conflict, Assert.IsType<DomainException>(Assert.Single(results, x => x is not null)).Code);
        await using var read = database.CreateContext();
        var remaining = await read.EventOwners.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Contains(remaining.UserId, new[] { seed.User.Id, seed.Other.Id });
        Assert.True(await read.Users.AnyAsync(x => x.Id == remaining.UserId && x.IsEligible && x.DepartureVerifiedUtc == null));
        Assert.True(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id &&
            x.UserId == remaining.UserId && x.Status == MembershipStatus.Active));
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Ownership.Removed").ToListAsync());
    }

    /// <summary>Restricts requester/recipient history to its identity and masks historical Event names when membership never existed.</summary>
    /// <returns>A task completing after direct denied mutations, scoped lists and historical-name checks.</returns>
    [Fact]
    public async Task ConsentHistoryIsIdentityScopedAndMasksHistoricalNames()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var stranger = FoundationSeed.NewUser();
        stranger.TenantId = seed.User.TenantId;
        await FoundationSeed.PersistAsync(database, stranger);
        var recipient = context.Service(seed.Other);
        await recipient.RequestMembershipAsync(seed.Event.Id);
        await context.Service(seed.User).InviteAsync(seed.Event.Id, seed.Other.ObjectId);
        var request = Assert.Single((await recipient.ListRequestsAsync(null, new())).Items);
        var invitation = Assert.Single((await recipient.ListInvitationsAsync(null, new())).Items);
        var unrelated = context.Service(stranger);
        Assert.Empty((await unrelated.ListRequestsAsync(null, new())).Items);
        Assert.Empty((await unrelated.ListInvitationsAsync(null, new())).Items);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => unrelated.WithdrawRequestAsync(request.Id))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => unrelated.RespondToInvitationAsync(invitation.Id, true))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => recipient.ListRequestsAsync(seed.Event.Id, new()))).Code);
        await using (var setup = database.CreateContext())
        {
            (await setup.Events.FindAsync(seed.Event.Id))!.Status = EventStatus.Completed;
            await setup.SaveChangesAsync();
        }
        var hiddenRequest = Assert.Single((await recipient.ListRequestsAsync(null, new())).Items);
        var hiddenInvitation = Assert.Single((await recipient.ListInvitationsAsync(null, new())).Items);
        Assert.Equal("Unavailable Event", hiddenRequest.EventName);
        Assert.Equal(MembershipRequestStatus.Rejected, hiddenRequest.Status);
        Assert.Equal("Unavailable Event", hiddenInvitation.EventName);
        Assert.Equal(EventInvitationStatus.Expired, hiddenInvitation.Status);
        await using var read = database.CreateContext();
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId != seed.User.Id));
        Assert.Equal(MembershipRequestStatus.Pending, (await read.MembershipRequests.SingleAsync(x => x.Id == request.Id)).Status);
    }

    /// <summary>Serializes competing owner approvals into one membership, one logical decision envelope and one retained approved request.</summary>
    /// <returns>A task completing after both real SQL commands and exact effect-count assertions.</returns>
    [Fact]
    public async Task ConcurrentApprovalsProduceOneMembershipAndDecision()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var requester = FoundationSeed.NewUser();
        requester.TenantId = seed.User.TenantId;
        await FoundationSeed.PersistAsync(database, requester);
        await context.Service(seed.User).AddOwnerAsync(seed.Event.Id, seed.Other.ObjectId);
        await context.Service(requester).RequestMembershipAsync(seed.Event.Id);
        var request = Assert.Single((await context.Service(requester).ListRequestsAsync(null, new())).Items);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ApproveAsync(UserAccount owner)
        {
            await gate.Task;
            await context.Service(owner).DecideRequestAsync(request.Id, true, "Approved");
        }
        var first = ApproveAsync(seed.User);
        var second = ApproveAsync(seed.Other);
        gate.SetResult();
        await Task.WhenAll(first, second);
        await using var read = database.CreateContext();
        var membership = await read.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id && x.UserId == requester.Id);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Contains(membership.ChangedById, new[] { seed.User.Id, seed.Other.Id });
        var saved = await read.MembershipRequests.SingleAsync(x => x.Id == request.Id);
        Assert.Equal(MembershipRequestStatus.Approved, saved.Status);
        Assert.Equal(membership.ChangedById, saved.DecidedById);
        Assert.Equal("Approved", saved.Reason);
        var decisionAudit = await read.AuditEntries.SingleAsync(x => x.ResourceId == seed.Event.Id &&
            x.Action == "Membership.Activated" && x.Reason.Contains(requester.Id.ToString("N")));
        var envelope = await read.OutboxMessages.SingleAsync(x => x.CorrelationId == decisionAudit.CorrelationId);
        Assert.Equal(seed.Event.Id, envelope.AggregateId);
        Assert.Contains(requester.Id.ToString(), envelope.PayloadJson);
        Assert.Empty(context.Quests.Calls);
    }
}
