using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Checks retained invitation semantics, authorization and atomic failure through the real Quest service.</summary>
/// <param name="database">The uniquely owned migrated SQL catalog for these independent scenarios.</param>
public sealed class QuestInvitationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>An Active grant is unchanged; a Revoked grant reuses the same row and publishes one new intent without restoring participation.</summary>
    /// <param name="status">The existing invitation state before the owner repeats or renews the grant.</param>
    /// <returns>Completion after identity, actor, rowversion, audit, outbox and private access assertions.</returns>
    [Theory]
    [InlineData(QuestInvitationStatus.Active)]
    [InlineData(QuestInvitationStatus.Revoked)]
    public async Task ExistingInvitation_PreservesIdentityAndRepeatSafety(QuestInvitationStatus status)
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var invitation = new QuestInvitation
        {
            QuestId = scenario.Seed.Quest.Id, UserId = scenario.Seed.Other.Id,
            InvitedById = scenario.Seed.Other.Id, ChangedUtc = FoundationSeed.Now.AddDays(-1), Status = status
        };
        await FoundationSeed.PersistAsync(database, invitation);
        var service = scenario.Service();
        await service.InviteAsync(invitation.QuestId, invitation.UserId);
        await service.InviteAsync(invitation.QuestId, invitation.UserId);
        Assert.Equal(ParticipationStatus.None,
            (await scenario.Service(scenario.Seed.Other).GetAsync(invitation.QuestId)).Summary.Participation);
        await using var read = database.CreateContext();
        var current = Assert.Single(await read.QuestInvitations.Where(x => x.QuestId == invitation.QuestId).ToListAsync());
        Assert.Equal(invitation.Id, current.Id);
        Assert.Equal(QuestInvitationStatus.Active, current.Status);
        Assert.Equal(status == QuestInvitationStatus.Active ? invitation.InvitedById : scenario.Seed.User.Id, current.InvitedById);
        Assert.Equal(status == QuestInvitationStatus.Active ? invitation.ChangedUtc : scenario.Clock.GetUtcNow(), current.ChangedUtc);
        if (status == QuestInvitationStatus.Active)
            Assert.Equal(invitation.Version, current.Version);
        else
            Assert.False(invitation.Version.SequenceEqual(current.Version));
        var expectedChanges = status == QuestInvitationStatus.Active ? 0 : 1;
        Assert.Equal(expectedChanges, await read.AuditEntries.CountAsync(x => x.ResourceId == invitation.QuestId));
        Assert.Equal(expectedChanges, await read.OutboxMessages.CountAsync(x => x.AggregateId == invitation.QuestId));
        Assert.Empty(await read.Participations.Where(x => x.QuestId == invitation.QuestId).ToListAsync());
    }

    /// <summary>An invited member can read but cannot invite others or use the distinct moderation path.</summary>
    /// <returns>Completion after both owner/moderation denials and proof that unauthorized mutation never reaches the reservation.</returns>
    [Fact]
    public async Task Invitation_DoesNotGrantOwnershipOrModeration()
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        await using (var setup = database.CreateContext())
        {
            setup.EventOwners.Remove(await setup.EventOwners.SingleAsync(x => x.EventId == scenario.Seed.Event.Id));
            await setup.SaveChangesAsync();
        }
        await scenario.Service().InviteAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id);
        var gate = new InvitationReadGate();
        gate.Release.TrySetResult();
        var guest = new QuestService(new ObservedContextFactory(database, gate),
            new ResourceAccess(StubCurrentUser.For(scenario.Seed.Other)), new ChangeWriter(), scenario.Clock, scenario.Reconciler);
        Assert.False((await guest.GetAsync(scenario.Seed.Quest.Id)).Summary.IsOwner);
        var denied = await Assert.ThrowsAsync<DomainException>(() => guest.InviteAsync(scenario.Seed.Quest.Id, scenario.Seed.User.Id));
        Assert.Equal(ErrorCode.NotFound, denied.Code);
        Assert.False(gate.Started.Task.IsCompleted);
        Assert.Equal(ErrorCode.NotFound,
            (await Assert.ThrowsAsync<DomainException>(() => guest.GetAsync(scenario.Seed.Quest.Id, true))).Code);
    }

    /// <summary>Failure or cancellation after all invitation SQL writes rolls back the grant, audit, outbox and Quest update together.</summary>
    /// <param name="cancel">Whether the injected post-save failure is cancellation rather than an operational exception.</param>
    /// <returns>Completion after propagation, unchanged persisted state, and a successful subsequent explicit invitation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostSaveFailure_RollsBackGrantAndIntent(bool cancel)
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        Exception failure = cancel ? new OperationCanceledException("Controlled invitation cancellation.") :
            new InvalidOperationException("Controlled invitation failure.");
        var observer = new FailAfterInvitationSave(failure);
        var service = new QuestService(new ObservedContextFactory(database, observer),
            new ResourceAccess(StubCurrentUser.For(scenario.Seed.User)), new ChangeWriter(), scenario.Clock, scenario.Reconciler);
        var id = scenario.Seed.Quest.Id;
        var version = (await scenario.Service().GetAsync(id)).Summary.Version;
        Assert.Same(failure, await Record.ExceptionAsync(() => service.InviteAsync(id, scenario.Seed.Other.Id)));
        Assert.True(observer.Saved);
        await using (var read = database.CreateContext())
        {
            Assert.Empty(await read.QuestInvitations.Where(x => x.QuestId == id).ToListAsync());
            Assert.Empty(await read.AuditEntries.Where(x => x.ResourceId == id).ToListAsync());
            Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync());
            Assert.Equal(version, Convert.ToBase64String((await read.Quests.SingleAsync(x => x.Id == id)).Version));
        }
        Assert.Equal(ErrorCode.NotFound,
            (await Assert.ThrowsAsync<DomainException>(() => scenario.Service(scenario.Seed.Other).GetAsync(id))).Code);
        await scenario.Service().InviteAsync(id, scenario.Seed.Other.Id);
        Assert.Equal(id, (await scenario.Service(scenario.Seed.Other).GetAsync(id)).Summary.Id);
    }

    private sealed class FailAfterInvitationSave(Exception failure) : SaveChangesInterceptor
    {
        internal bool Saved { get; private set; }

        /// <inheritdoc />
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(4, result);
            Assert.Equal(QuestInvitationStatus.Active,
                Assert.Single(eventData.Context!.ChangeTracker.Entries<QuestInvitation>()).Entity.Status);
            Saved = true;
            throw failure;
        }
    }
}
