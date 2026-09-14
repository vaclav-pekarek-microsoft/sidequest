using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Events.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Verifies SQL rowversion ordering prevents stale bulk invitations despite equal timestamps or clock skew.</summary>
/// <param name="database">Uniquely owned migrated SQL fixture for the sequential cases.</param>
public sealed class BulkOrderingTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>A removal before the batch permits a new consent invitation even when both operations have the same UTC timestamp.</summary>
    /// <returns>A task completing after fresh invitation, retained removed membership, and applied recipient assertions.</returns>
    [Fact]
    public async Task EarlierRemovalCanBeInvitedAgainAtSameTimestamp()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var owner = context.Service(seed.User);
        await owner.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false);
        await owner.RemoveMemberAsync(seed.Event.Id, seed.Other.Id, "Earlier explicit removal.");
        var operation = await owner.StartBulkAsync(seed.Event.Id, Guid.NewGuid(), BulkMode.Invite);
        var work = await FindWorkAsync(operation);
        await context.Bulk().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        Assert.Equal(BulkRecipientStatus.Applied, (await read.BulkRecipients.SingleAsync(x => x.OperationId == operation)).Status);
        Assert.Equal(EventInvitationStatus.Pending, (await read.EventInvitations.SingleAsync(x => x.EventId == seed.Event.Id)).Status);
        var membership = await read.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id);
        Assert.Equal(MembershipStatus.Removed, membership.Status);
        Assert.Equal(FoundationSeed.Now, membership.ChangedUtc);
        Assert.Equal(FoundationSeed.Now, (await read.BulkOperations.FindAsync(operation))!.CreatedUtc);
        var payload = JsonSerializer.Deserialize<BulkMembershipPayload>(work.PayloadJson)!;
        Assert.True(membership.Version.AsSpan().SequenceCompareTo(Convert.FromBase64String(payload.StartedVersion)) < 0);
    }

    /// <summary>A later removal or invitation revocation wins even when that host's clock is behind the initiating host's clock.</summary>
    /// <param name="revoke">Whether the later access decision revokes a pending invitation instead of removing membership.</param>
    /// <returns>A task completing after skipped recipient, no new invitation, and retained removal assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterRemovalOrRevocationWinsDespiteClockSkew(bool revoke)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var owner = context.Service(seed.User);
        Guid? invitationId = null;
        if (revoke)
        {
            await owner.InviteAsync(seed.Event.Id, seed.Other.ObjectId);
            invitationId = Assert.Single((await owner.ListInvitationsAsync(seed.Event.Id, new())).Items).Id;
        }
        else
        {
            await owner.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false);
        }
        var operation = await owner.StartBulkAsync(seed.Event.Id, Guid.NewGuid(), BulkMode.Invite);
        var work = await FindWorkAsync(operation);
        context.Clock.Now = FoundationSeed.Now.AddMinutes(-1);
        if (revoke)
            await owner.RevokeInvitationAsync(invitationId!.Value);
        else
            await owner.RemoveMemberAsync(seed.Event.Id, seed.Other.Id, "A later removal on a clock-skewed host.");
        context.Clock.Now = FoundationSeed.Now;
        await context.Bulk().ExecuteAsync(work.Id, default);
        await context.Bulk().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        Assert.Equal(BulkRecipientStatus.Skipped, (await read.BulkRecipients.SingleAsync(x => x.OperationId == operation)).Status);
        Assert.Empty(await read.EventInvitations.Where(x => x.EventId == seed.Event.Id && x.Status == EventInvitationStatus.Pending).ToListAsync());
        var payload = JsonSerializer.Deserialize<BulkMembershipPayload>(work.PayloadJson)!;
        var started = (await read.BulkOperations.FindAsync(operation))!.CreatedUtc;
        if (revoke)
        {
            var invitation = await read.EventInvitations.SingleAsync(x => x.EventId == seed.Event.Id);
            Assert.Equal(EventInvitationStatus.Revoked, invitation.Status);
            Assert.True(invitation.ResolvedUtc < started);
            Assert.True(invitation.Version.AsSpan().SequenceCompareTo(Convert.FromBase64String(payload.StartedVersion)) > 0);
            Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        }
        else
        {
            var membership = await read.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id);
            Assert.Equal(MembershipStatus.Removed, membership.Status);
            Assert.True(membership.ChangedUtc < started);
            Assert.True(membership.Version.AsSpan().SequenceCompareTo(Convert.FromBase64String(payload.StartedVersion)) > 0);
        }
        Assert.Equal(1, context.Directory.ExpansionCalls);
    }

    /// <summary>An older two-field v1 payload is handled conservatively and cannot recreate access or invitations after historical removal.</summary>
    /// <returns>A task completing after legacy deserialization and explicit skipped-recipient assertions.</returns>
    [Fact]
    public async Task LegacyPayloadUsesConservativeRevocationFence()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var owner = context.Service(seed.User);
        await owner.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false);
        await owner.RemoveMemberAsync(seed.Event.Id, seed.Other.Id, "Prior membership removal.");
        var operation = await owner.StartBulkAsync(seed.Event.Id, Guid.NewGuid(), BulkMode.Invite);
        var work = await FindWorkAsync(operation);
        await using (var setup = database.CreateContext())
        {
            (await setup.ScheduledWork.FindAsync(work.Id))!.PayloadJson =
                $$"""{"SchemaVersion":1,"OperationId":"{{operation:D}}"}""";
            await setup.SaveChangesAsync();
        }
        await context.Bulk().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        Assert.Equal(BulkRecipientStatus.Skipped, (await read.BulkRecipients.SingleAsync(x => x.OperationId == operation)).Status);
        Assert.Empty(await read.EventInvitations.Where(x => x.EventId == seed.Event.Id).ToListAsync());
        Assert.Equal(MembershipStatus.Removed, (await read.EventMemberships.SingleAsync(x =>
            x.EventId == seed.Event.Id && x.UserId == seed.Other.Id)).Status);
    }

    /// <summary>Malformed or impossible creation fences fail before any directory expansion or recipient application.</summary>
    /// <param name="version">Invalid Base64, wrong length, empty value, or a SQL rowversion newer than the operation.</param>
    /// <returns>A task completing after safe validation and no-side-effect assertions.</returns>
    [Theory]
    [InlineData("invalid")]
    [InlineData("AA==")]
    [InlineData("")]
    [InlineData("//////////8=")]
    public async Task InvalidCreationFenceCannotApplyRecipients(string version)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var operation = await context.Service(seed.User).StartBulkAsync(seed.Event.Id, Guid.NewGuid(), BulkMode.Add);
        var work = await FindWorkAsync(operation);
        await using (var setup = database.CreateContext())
        {
            var row = (await setup.ScheduledWork.FindAsync(work.Id))!;
            row.PayloadJson = JsonSerializer.Serialize(new BulkMembershipPayload(1, operation, version));
            await setup.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            context.Bulk().ExecuteAsync(work.Id, default))).Code);
        Assert.Equal(0, context.Directory.ExpansionCalls);
        await using var read = database.CreateContext();
        Assert.Empty(await read.BulkRecipients.Where(x => x.OperationId == operation).ToListAsync());
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
    }

    private async Task<ScheduledWork> FindWorkAsync(Guid operation)
    {
        await using var db = database.CreateContext();
        return await db.ScheduledWork.AsNoTracking().SingleAsync(x => x.PayloadJson.Contains(operation.ToString()));
    }
}
