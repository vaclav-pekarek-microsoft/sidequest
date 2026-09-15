using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks identity-bound resource authorization, privacy-safe denial, equal ownership, and next-call revocation against SQL.</summary>
/// <param name="database">The class-owned migrated catalog, with independent users and aggregates per scenario.</param>
public sealed class ResourceAccessTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    private const string Unavailable = "This resource is unavailable.";
    private const string Ineligible = "Your account is not eligible for Sidequest.";
    private const string SignIn = "Sign in to continue.";
    private static readonly QuestStatus[] Published =
        [QuestStatus.Active, QuestStatus.Suspended, QuestStatus.Completed, QuestStatus.Cancelled, QuestStatus.Archived];

    /// <summary>Ordinary Quest-owner authorization does not read irrelevant history ranges that can deadlock independent publications.</summary>
    /// <param name="ownerOnly">Whether the caller also explicitly requires ownership for a mutation.</param>
    /// <returns>A task completing after successful locked owner authorization and proof that a real competing history lock remains held.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuestOwnerAuthorization_DoesNotAcquireIrrelevantHistoryRangeLocks(bool ownerOnly)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await GrantAsync(seed, eventOwner: true, questOwner: true);
        await ConfigureAsync(seed, status: QuestStatus.Draft);
        await using var blocker = database.CreateContext();
        await using var blockedHistory = await blocker.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM [QuestStatusHistory] WITH (TABLOCKX, HOLDLOCK)");

        await using var db = database.CreateContext();
        await using var transaction = await db.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 1000");
        await db.LockEventAsync(seed.Event.Id);
        var result = await new ResourceAccess(StubCurrentUser.For(seed.User))
            .RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, ownerOnly);
        Assert.Equal(seed.Quest.Id, result.Id);
        Assert.Equal(QuestStatus.Draft, result.Status);
        Assert.Equal("Private quest details", result.Description);
        Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var control = db.Database.GetDbConnection().CreateCommand();
        control.Transaction = transaction.GetDbTransaction();
        control.CommandText = "SELECT COUNT_BIG(*) FROM [QuestStatusHistory]";
        var blocked = await Assert.ThrowsAsync<SqlException>(() => control.ExecuteScalarAsync());
        Assert.Equal(1222, blocked.Number);
    }

    /// <summary>Checks that changed contact fields cannot replace tenant/object identity or its existing resource grants.</summary>
    /// <returns>A task completing after identity re-resolution, exact account/resource assertions, and no-write checks.</returns>
    [Fact]
    public async Task RequireUser_ResolvesTenantObject_NotContactFields()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await GrantAsync(seed, eventOwner: true, questOwner: true);
        var identity = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(identity);
        await using (var db = database.CreateContext())
        {
            var user = await service.RequireUserAsync(db);
            Assert.Equal(seed.User.Id, user.Id);
            Assert.Equal("ada@example.invalid", user.Email);
        }
        await using (var update = database.CreateContext())
        {
            var user = await update.Users.SingleAsync(x => x.Id == seed.User.Id);
            user.Email = "new-contact@example.invalid";
            user.DisplayName = "Renamed account";
            await update.SaveChangesAsync();
        }
        identity.Identity = identity.Identity! with { Email = "another@example.invalid", DisplayName = "Another identity label" };
        await using var read = database.CreateContext();
        var resolved = await service.RequireUserAsync(read);
        Assert.Equal(seed.User.Id, resolved.Id);
        Assert.Equal("new-contact@example.invalid", resolved.Email);
        Assert.Equal("Renamed account", resolved.DisplayName);
        Assert.Equal(seed.Event.Id, (await service.RequireEventAsync(read, seed.Event.Id, resolved.Id, true)).Id);
        Assert.Equal(seed.Quest.Id, (await service.RequireQuestAsync(read, seed.Quest.Id, resolved.Id, true)).Id);
        Assert.Equal(4, identity.Calls);
        Assert.False(read.ChangeTracker.HasChanges());
    }

    /// <summary>Checks Forbidden denial for unmatched or ineligible identities without provisioning users or administrators.</summary>
    /// <param name="mismatch">The object, tenant, both, or ineligible denial partition.</param>
    /// <returns>A task completing after exact safe-error and unchanged-account-count assertions.</returns>
    [Theory]
    [InlineData("object")]
    [InlineData("tenant")]
    [InlineData("both")]
    [InlineData("ineligible")]
    public async Task RequireUser_UnmatchedOrIneligibleIdentity_DeniesWithoutProvisioning(string mismatch)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var identity = new UserIdentity(seed.User.TenantId, seed.User.ObjectId, seed.User.DisplayName, seed.User.Email);
        if (mismatch == "ineligible")
            await SetEligibilityAsync(seed.User.Id, false);
        else
            identity = identity with
            {
                TenantId = mismatch is "tenant" or "both" ? Guid.NewGuid() : identity.TenantId,
                ObjectId = mismatch is "object" or "both" ? Guid.NewGuid() : identity.ObjectId
            };
        await using var db = database.CreateContext();
        var users = await db.Users.CountAsync();
        var admins = await db.Administrators.CountAsync();
        var fake = new StubCurrentUser(identity);
        await DeniedAsync(() => new ResourceAccess(fake).RequireUserAsync(db), ErrorCode.Forbidden, Ineligible);
        Assert.Equal(1, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        Assert.Equal(users, await read.Users.CountAsync());
        Assert.Equal(admins, await read.Administrators.CountAsync());
        Assert.Equal("ada@example.invalid", (await read.Users.SingleAsync(x => x.Id == seed.User.Id)).Email);
    }

    /// <summary>Checks sign-in denial for existing resources and privacy-safe NotFound for a missing Quest before identity lookup.</summary>
    /// <returns>A task completing after error, identity-call-count, and no-tracked-write assertions.</returns>
    [Fact]
    public async Task RequireUser_NoIdentity_RequiresSignIn()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var fake = new StubCurrentUser(null);
        var access = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        await DeniedAsync(() => access.RequireUserAsync(db), ErrorCode.Forbidden, SignIn);
        await DeniedAsync(() => access.RequireAdministratorAsync(db), ErrorCode.Forbidden, SignIn);
        await DeniedAsync(() => access.RequireEventAsync(db, seed.Event.Id, seed.User.Id), ErrorCode.Forbidden, SignIn);
        await DeniedAsync(() => access.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id), ErrorCode.Forbidden, SignIn);
        Assert.Equal(4, fake.Calls);
        await DeniedAsync(() => access.RequireQuestAsync(db, Guid.NewGuid(), seed.User.Id), ErrorCode.NotFound, Unavailable);
        Assert.Equal(4, fake.Calls); // Missing Quest is looked up before identity.
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Checks that administrator access requires both an eligible current account and its explicit Administrator row.</summary>
    /// <param name="scenario">The admin, nonadmin, ineligible, or anonymous partition.</param>
    /// <returns>A task completing after exact account-or-denial and side-effect assertions.</returns>
    [Theory]
    [InlineData("admin")]
    [InlineData("nonadmin")]
    [InlineData("ineligible")]
    [InlineData("anonymous")]
    public async Task RequireAdministrator_RequiresEligibleMatchingUserAndAdministratorRow(string scenario)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        if (scenario != "nonadmin")
            await FoundationSeed.PersistAsync(database, new Administrator { UserId = seed.User.Id });
        if (scenario == "ineligible")
            await SetEligibilityAsync(seed.User.Id, false);
        var fake = scenario == "anonymous" ? new StubCurrentUser(null) : StubCurrentUser.For(seed.User);
        await using var db = database.CreateContext();
        var access = new ResourceAccess(fake);
        if (scenario == "admin")
        {
            var user = await access.RequireAdministratorAsync(db);
            Assert.Equal(seed.User.Id, user.Id);
            Assert.Equal("ada@example.invalid", user.Email);
        }
        else
            await DeniedAsync(() => access.RequireAdministratorAsync(db), ErrorCode.Forbidden,
                scenario switch { "anonymous" => SignIn, "ineligible" => Ineligible, _ => "Administrator access is required." });
        Assert.Equal(1, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Checks all four access methods with two identities on one service and verifies every forwarded cancellation token.</summary>
    /// <returns>A task completing after resource/account identity, lookup-count, token, and no-write assertions.</returns>
    [Fact]
    public async Task ResourceMethods_ReResolveIdentityAndForwardCancellation()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await GrantAsync(seed);
        await FoundationSeed.PersistAsync(database, seed.Membership(seed.Other.Id),
            new Administrator { UserId = seed.User.Id }, new Administrator { UserId = seed.Other.Id });
        var fake = StubCurrentUser.For(seed.User);
        var access = new ResourceAccess(fake);
        using var cancellation = new CancellationTokenSource();
        await using var db = database.CreateContext();
        foreach (var user in new[] { seed.User, seed.Other })
        {
            fake.Identity = StubCurrentUser.For(user).Identity;
            Assert.Equal(user.Id, (await access.RequireUserAsync(db, cancellation.Token)).Id);
            Assert.Equal(user.Id, (await access.RequireAdministratorAsync(db, cancellation.Token)).Id);
            Assert.Equal(seed.Event.Id, (await access.RequireEventAsync(db, seed.Event.Id, user.Id, cancellationToken: cancellation.Token)).Id);
            Assert.Equal(seed.Quest.Id, (await access.RequireQuestAsync(db, seed.Quest.Id, user.Id, cancellationToken: cancellation.Token)).Id);
        }
        Assert.Equal(8, fake.Calls);
        Assert.All(fake.ObservedTokens, token => Assert.Equal(cancellation.Token, token));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Gets lifecycle, active/missing/removed membership, ownership, owner-only mode, and expected Event access rows.</summary>
    public static TheoryData<EventStatus, string, bool, bool, bool> EventAccessCases
    {
        get
        {
            var data = new TheoryData<EventStatus, string, bool, bool, bool>();
            foreach (var status in new[] { EventStatus.Active, EventStatus.Completed, EventStatus.Cancelled, EventStatus.Archived })
            {
                data.Add(status, "Active", false, false, true);
                data.Add(status, "Active", false, true, false);
                data.Add(status, "Active", true, false, true);
                data.Add(status, "Active", true, true, true);
                foreach (var membership in new[] { "None", "Removed" })
                {
                    data.Add(status, membership, false, false, false);
                    data.Add(status, membership, true, false, false);
                    data.Add(status, membership, true, true, false);
                }
            }
            foreach (var membership in new[] { "Active", "None", "Removed" })
            {
                data.Add(EventStatus.Draft, membership, false, false, false);
                data.Add(EventStatus.Draft, membership, false, true, false);
                // Owners must still have active individual membership (handoff D15).
                // Retain denial regressions even though M2 should prevent inconsistent owner rows.
                data.Add(EventStatus.Draft, membership, true, false, membership == "Active");
                data.Add(EventStatus.Draft, membership, true, true, membership == "Active");
            }
            return data;
        }
    }

    /// <summary>Checks ordinary and owner-only Event access without allowing ownership to bypass required individual membership.</summary>
    /// <param name="status">The persisted Event lifecycle state.</param>
    /// <param name="membership">The Active, Removed, or None individual membership partition.</param>
    /// <param name="owner">Whether the caller has an Event owner relation.</param>
    /// <param name="ownerOnly">Whether the requested operation requires Event ownership.</param>
    /// <param name="allowed">The independently specified expected access result.</param>
    /// <returns>A task completing after permission, lookup, and unchanged Event-state assertions.</returns>
    [Theory]
    [MemberData(nameof(EventAccessCases))]
    public async Task RequireEvent_StatusMembershipAndOwnerOnlyMatrix(EventStatus status, string membership, bool owner, bool ownerOnly, bool allowed)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, eventStatus: status);
        await GrantAsync(seed, membership, owner);
        var fake = StubCurrentUser.For(seed.User);
        await using var db = database.CreateContext();
        await CheckResourceAsync(() => new ResourceAccess(fake).RequireEventAsync(db, seed.Event.Id, seed.User.Id, ownerOnly),
            allowed, seed.Event.Id);
        Assert.Equal(1, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        Assert.Equal(status, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
    }

    /// <summary>Gets Quest lifecycle/visibility/grant combinations with literal ordinary-read permissions.</summary>
    public static TheoryData<QuestStatus, QuestVisibility, string, bool> QuestReadCases
    {
        get
        {
            var data = new TheoryData<QuestStatus, QuestVisibility, string, bool>();
            foreach (var status in Published)
            {
                foreach (var relation in new[] { "none", "revoked", "eventOwner" })
                {
                    data.Add(status, QuestVisibility.Public, relation, true);
                    data.Add(status, QuestVisibility.Private, relation, false);
                }
                foreach (var relation in new[] { "questOwner", "invited", "ownerAndInvited" })
                {
                    data.Add(status, QuestVisibility.Public, relation, true);
                    data.Add(status, QuestVisibility.Private, relation, true);
                }
            }
            foreach (var visibility in new[] { QuestVisibility.Public, QuestVisibility.Private })
            {
                foreach (var relation in new[] { "none", "revoked", "eventOwner", "invited" })
                    data.Add(QuestStatus.Draft, visibility, relation, false);
                data.Add(QuestStatus.Draft, visibility, "questOwner", true);
                data.Add(QuestStatus.Draft, visibility, "ownerAndInvited", true);
            }
            return data;
        }
    }

    /// <summary>Checks ordinary Quest access and prevents an invitation from becoming editing permission or participation.</summary>
    /// <param name="status">The Quest lifecycle state.</param>
    /// <param name="visibility">The public or private Quest visibility.</param>
    /// <param name="relation">The absent, revoked, Event-owner, Quest-owner, invitation, or combined Quest grant partition.</param>
    /// <param name="allowed">The literal expected ordinary-read result.</param>
    /// <returns>A task completing after access, editing-denial, and unchanged participation/status assertions.</returns>
    [Theory]
    [MemberData(nameof(QuestReadCases))]
    public async Task RequireQuest_OrdinaryRead_StatusVisibilityAndRelations(QuestStatus status, QuestVisibility visibility, string relation, bool allowed)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, status, visibility);
        await GrantAsync(seed, eventOwner: relation == "eventOwner",
            questOwner: relation is "questOwner" or "ownerAndInvited",
            invitation: relation switch { "invited" or "ownerAndInvited" => "Active", "revoked" => "Revoked", _ => "None" });
        var fake = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        await CheckResourceAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id), allowed, seed.Quest.Id);
        if (relation == "invited")
            await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, true), ErrorCode.NotFound, Unavailable);
        Assert.Equal(relation == "invited" ? 2 : 1, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        Assert.False(await read.Participations.AnyAsync(x => x.QuestId == seed.Quest.Id));
        Assert.Equal(status, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
    }

    /// <summary>Checks that Event ownership or creation alone cannot edit a Quest, including through the moderation flag.</summary>
    /// <param name="visibility">The Quest visibility.</param>
    /// <param name="moderation">Whether the request uses dedicated Event moderation.</param>
    /// <param name="creator">Whether to test creator-only provenance instead of Event ownership.</param>
    /// <returns>A task completing after denied editing and explicit Quest-owner positive-control assertions.</returns>
    [Theory]
    [InlineData(QuestVisibility.Public, false, false)]
    [InlineData(QuestVisibility.Public, true, false)]
    [InlineData(QuestVisibility.Private, false, false)]
    [InlineData(QuestVisibility.Private, true, false)]
    [InlineData(QuestVisibility.Public, false, true)]
    [InlineData(QuestVisibility.Public, true, true)]
    [InlineData(QuestVisibility.Private, false, true)]
    [InlineData(QuestVisibility.Private, true, true)]
    public async Task RequireQuest_EventOwnershipNeverGrantsQuestEditing(QuestVisibility visibility, bool moderation, bool creator)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, visibility: visibility);
        // A creator without either owner row is independent of the Event-only-owner partition.
        if (creator)
        {
            await using var update = database.CreateContext();
            (await update.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).CreatorId = seed.User.Id;
            (await update.Events.SingleAsync(x => x.Id == seed.Event.Id)).CreatorId = seed.User.Id;
            await update.SaveChangesAsync();
        }
        await GrantAsync(seed, eventOwner: !creator);
        var service = new ResourceAccess(StubCurrentUser.For(seed.User));
        await using (var db = database.CreateContext())
        {
            await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, true, moderation), ErrorCode.NotFound, Unavailable);
            if (creator)
                await DeniedAsync(() => service.RequireEventAsync(db, seed.Event.Id, seed.User.Id, true), ErrorCode.NotFound, Unavailable);
        }
        await FoundationSeed.PersistAsync(database, new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id });
        await using var read = database.CreateContext();
        await CheckResourceAsync(() => service.RequireQuestAsync(read, seed.Quest.Id, seed.User.Id, true, moderation),
            !creator || !moderation, seed.Quest.Id);
        Assert.Equal(1, await read.QuestOwners.CountAsync(x => x.QuestId == seed.Quest.Id && x.UserId == seed.User.Id));
        Assert.False(read.ChangeTracker.HasChanges());
    }

    /// <summary>Gets all non-Draft Quest lifecycle states at both visibilities for dedicated moderation checks.</summary>
    public static TheoryData<QuestStatus, QuestVisibility> ModerationCases
    {
        get
        {
            var data = new TheoryData<QuestStatus, QuestVisibility>();
            foreach (var status in Published)
                foreach (var visibility in new[] { QuestVisibility.Public, QuestVisibility.Private })
                    data.Add(status, visibility);
            return data;
        }
    }

    /// <summary>Checks Event-owner moderation reads without editing rights until an independent Quest-owner grant exists.</summary>
    /// <param name="status">The published or historical non-Draft Quest state.</param>
    /// <param name="visibility">The public or private visibility to moderate.</param>
    /// <returns>A task completing after read/edit permission and absent automatic invitation/participation assertions.</returns>
    [Theory]
    [MemberData(nameof(ModerationCases))]
    public async Task RequireQuest_ModerationIsPublishedEventOwnerRead_NotEdit(QuestStatus status, QuestVisibility visibility)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, status, visibility);
        await GrantAsync(seed, eventOwner: true);
        var service = new ResourceAccess(StubCurrentUser.For(seed.User));
        await using (var db = database.CreateContext())
        {
            Assert.Equal(seed.Quest.Id, (await service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, moderation: true)).Id);
            await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, true, true), ErrorCode.NotFound, Unavailable);
            Assert.False(db.ChangeTracker.HasChanges());
        }
        await FoundationSeed.PersistAsync(database, new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id });
        await using var read = database.CreateContext();
        Assert.Equal(seed.Quest.Id, (await service.RequireQuestAsync(read, seed.Quest.Id, seed.User.Id, true, true)).Id);
        Assert.False(await read.QuestInvitations.AnyAsync(x => x.QuestId == seed.Quest.Id));
        Assert.False(await read.Participations.AnyAsync(x => x.QuestId == seed.Quest.Id));
    }

    /// <summary>Checks each missing moderation gate despite a retained active private Quest invitation.</summary>
    /// <param name="gate">The Draft, owner, membership, or eligibility denial partition to construct.</param>
    /// <returns>A task completing after exact safe denial and unchanged grant assertions.</returns>
    [Theory]
    [InlineData("draft")]
    [InlineData("noEventOwner")]
    [InlineData("questOwnerOnly")]
    [InlineData("noMembership")]
    [InlineData("removed")]
    [InlineData("ineligible")]
    public async Task RequireQuest_ModerationMissingGate_Denies(string gate)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, gate == "draft" ? QuestStatus.Draft : QuestStatus.Active, QuestVisibility.Private);
        await GrantAsync(seed, gate switch { "noMembership" => "None", "removed" => "Removed", _ => "Active" },
            gate is not ("noEventOwner" or "questOwnerOnly"), gate == "questOwnerOnly", "Active");
        if (gate == "ineligible")
            await SetEligibilityAsync(seed.User.Id, false);
        await using var db = database.CreateContext();
        await DeniedAsync(() => new ResourceAccess(StubCurrentUser.For(seed.User)).RequireQuestAsync(db,
            seed.Quest.Id, seed.User.Id, moderation: true),
            gate == "ineligible" ? ErrorCode.Forbidden : ErrorCode.NotFound, gate == "ineligible" ? Ineligible : Unavailable);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.True(await db.QuestInvitations.AnyAsync(x => x.QuestId == seed.Quest.Id && x.Status == QuestInvitationStatus.Active));
    }

    /// <summary>Checks that Quest ownership and an active invitation cannot bypass parent access or a Draft-parent ordinary-read guard.</summary>
    /// <param name="gate">The missing/removed membership, ineligibility, or Draft-parent partition.</param>
    /// <param name="eventOwner">Whether an additional Event owner relation remains present.</param>
    /// <returns>A task completing after safe denial and retained-grant/no-write assertions.</returns>
    [Theory]
    [InlineData("missingMember", false)]
    [InlineData("missingMember", true)]
    [InlineData("removed", false)]
    [InlineData("removed", true)]
    [InlineData("ineligible", false)]
    [InlineData("ineligible", true)]
    [InlineData("draftParent", false)]
    [InlineData("draftParent", true)]
    public async Task RequireQuest_ParentEventAccessCannotBeBypassed(string gate, bool eventOwner)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, visibility: QuestVisibility.Private,
            eventStatus: gate == "draftParent" ? EventStatus.Draft : EventStatus.Active);
        await GrantAsync(seed, gate switch { "missingMember" => "None", "removed" => "Removed", _ => "Active" },
            eventOwner, true, "Active");
        if (gate == "ineligible")
            await SetEligibilityAsync(seed.User.Id, false);
        await using var db = database.CreateContext();
        var service = new ResourceAccess(StubCurrentUser.For(seed.User));
        await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id),
            gate == "ineligible" ? ErrorCode.Forbidden : ErrorCode.NotFound, gate == "ineligible" ? Ineligible : Unavailable);
        Assert.True(await db.QuestOwners.AnyAsync(x => x.QuestId == seed.Quest.Id && x.UserId == seed.User.Id));
        Assert.True(await db.QuestInvitations.AnyAsync(x => x.QuestId == seed.Quest.Id && x.Status == QuestInvitationStatus.Active));
        Assert.False(db.ChangeTracker.HasChanges());
        // Draft parents cannot acquire Quests through the accepted M2 workflow. This is
        // a defensive ordinary-read predicate test, not proof of a supported row-creation route.
    }

    /// <summary>Checks that administrator status confers no Event/Quest content, editing, or moderation permission.</summary>
    /// <returns>A task completing after denied privileged routes and explicit ordinary-grant controls.</returns>
    [Fact]
    public async Task AdministratorRow_DoesNotGrantContentAccess()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, visibility: QuestVisibility.Private);
        await FoundationSeed.PersistAsync(database, new Administrator { UserId = seed.User.Id });
        var service = new ResourceAccess(StubCurrentUser.For(seed.User));
        await using (var db = database.CreateContext())
        {
            Assert.Equal(seed.User.Id, (await service.RequireAdministratorAsync(db)).Id);
            await DeniedAsync(() => service.RequireEventAsync(db, seed.Event.Id, seed.User.Id), ErrorCode.NotFound, Unavailable);
            await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id), ErrorCode.NotFound, Unavailable);
        }
        await GrantAsync(seed, invitation: "Active");
        await using var read = database.CreateContext();
        Assert.Equal(seed.Event.Id, (await service.RequireEventAsync(read, seed.Event.Id, seed.User.Id)).Id);
        Assert.Equal(seed.Quest.Id, (await service.RequireQuestAsync(read, seed.Quest.Id, seed.User.Id)).Id);
        await DeniedAsync(() => service.RequireEventAsync(read, seed.Event.Id, seed.User.Id, true), ErrorCode.NotFound, Unavailable);
        await DeniedAsync(() => service.RequireQuestAsync(read, seed.Quest.Id, seed.User.Id, true), ErrorCode.NotFound, Unavailable);
        await DeniedAsync(() => service.RequireQuestAsync(read, seed.Quest.Id, seed.User.Id, moderation: true), ErrorCode.NotFound, Unavailable);
        Assert.False(read.ChangeTracker.HasChanges());
    }

    /// <summary>Checks immediate eligibility loss on the same tracked service/context despite retained administrator and owner rows.</summary>
    /// <returns>A task completing after a separate-context eligibility change and every privileged-route denial.</returns>
    [Fact]
    public async Task IneligibleIdentity_DeniesAllPrivilegedRoutes()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await GrantAsync(seed, eventOwner: true, questOwner: true, invitation: "Active");
        await FoundationSeed.PersistAsync(database, new Administrator { UserId = seed.User.Id });
        var fake = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(fake);
        // Keep the tracked eligible account to detect accidental cached-identity authorization.
        await using var db = database.CreateContext();
        Assert.Equal(seed.User.Id, (await service.RequireAdministratorAsync(db)).Id);
        await SetEligibilityAsync(seed.User.Id, false);
        await DeniedAsync(() => service.RequireAdministratorAsync(db), ErrorCode.Forbidden, Ineligible);
        await DeniedAsync(() => service.RequireEventAsync(db, seed.Event.Id, seed.User.Id, true), ErrorCode.Forbidden, Ineligible);
        await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, true), ErrorCode.Forbidden, Ineligible);
        await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, moderation: true), ErrorCode.Forbidden, Ineligible);
        Assert.Equal(5, fake.Calls);
        await using var read = database.CreateContext();
        Assert.False((await read.Users.SingleAsync(x => x.Id == seed.User.Id)).IsEligible);
        Assert.True(await read.Administrators.AnyAsync(x => x.UserId == seed.User.Id));
        Assert.True(await read.QuestOwners.AnyAsync(x => x.QuestId == seed.Quest.Id && x.UserId == seed.User.Id));
    }

    /// <summary>Checks that passing another privileged user's ID cannot impersonate that user through any resource-access mode.</summary>
    /// <param name="resource">The event or quest access entry point.</param>
    /// <param name="ownerOnly">Whether the call requires the corresponding owner relation.</param>
    /// <param name="moderation">Whether the Quest call uses dedicated Event moderation.</param>
    /// <returns>A task completing after spoof denial, current-user positive control, and unchanged relation-count assertions.</returns>
    [Theory]
    [InlineData("event", false, false)]
    [InlineData("event", true, false)]
    [InlineData("quest", false, false)]
    [InlineData("quest", true, false)]
    [InlineData("quest", false, true)]
    [InlineData("quest", true, true)]
    public async Task ResourceMethods_ArbitraryUserIdCannotImpersonate(string resource, bool ownerOnly, bool moderation)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await GrantAsync(seed, eventOwner: true, questOwner: true, invitation: "Active");
        await FoundationSeed.PersistAsync(database, seed.Membership(seed.Other.Id),
            new EventOwner { EventId = seed.Event.Id, UserId = seed.Other.Id },
            new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.Other.Id });
        var fake = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        Task<Entity> Check(Guid user) => resource == "event"
            ? AsEntity(service.RequireEventAsync(db, seed.Event.Id, user, ownerOnly))
            : AsEntity(service.RequireQuestAsync(db, seed.Quest.Id, user, ownerOnly, moderation));
        await DeniedAsync(() => Check(seed.Other.Id), ErrorCode.NotFound, Unavailable);
        Assert.Equal(resource == "event" ? seed.Event.Id : seed.Quest.Id, (await Check(seed.User.Id)).Id);
        Assert.Equal(2, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        Assert.Equal(2, await read.EventMemberships.CountAsync(x => x.EventId == seed.Event.Id && x.Status == MembershipStatus.Active));
        Assert.Equal(2, await read.EventOwners.CountAsync(x => x.EventId == seed.Event.Id));
        Assert.Equal(2, await read.QuestOwners.CountAsync(x => x.QuestId == seed.Quest.Id));
        Assert.Equal(1, await read.QuestInvitations.CountAsync(x => x.QuestId == seed.Quest.Id && x.Status == QuestInvitationStatus.Active));
    }

    /// <summary>Checks indistinguishable safe NotFound content for missing and inaccessible resource IDs without private sentinels or identifiers.</summary>
    /// <returns>A task completing after exception-content, lookup-count, and no-write assertions.</returns>
    [Fact]
    public async Task UnavailableResources_ReturnSafeNotFoundWithoutPrivateData()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, visibility: QuestVisibility.Private);
        var fake = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        foreach (var id in new[] { seed.Event.Id, Guid.NewGuid() })
            AssertSafe(await DeniedAsync(() => service.RequireEventAsync(db, id, seed.User.Id), ErrorCode.NotFound, Unavailable), seed, id);
        foreach (var id in new[] { seed.Quest.Id, Guid.NewGuid() })
            AssertSafe(await DeniedAsync(() => service.RequireQuestAsync(db, id, seed.User.Id), ErrorCode.NotFound, Unavailable), seed, id);
        Assert.Equal(3, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Checks that pending requests/invitations and removed or missing membership do not act as individual Event membership.</summary>
    /// <param name="relation">The Active, Removed, None, Request, or Invitation parent relationship.</param>
    /// <param name="allowed">Whether both Event and invited private Quest reads must succeed.</param>
    /// <returns>A task completing after parent/child permission and retained Quest-invitation assertions.</returns>
    [Theory]
    [InlineData("Active", true)]
    [InlineData("Removed", false)]
    [InlineData("None", false)]
    [InlineData("Request", false)]
    [InlineData("Invitation", false)]
    public async Task OnlyActiveIndividualMembershipGrantsParentAccess(string relation, bool allowed)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, visibility: QuestVisibility.Private);
        await GrantAsync(seed, relation is "Active" or "Removed" ? relation : "None", invitation: "Active");
        if (relation == "Request")
            await FoundationSeed.PersistAsync(database, new EventMembershipRequest
            {
                EventId = seed.Event.Id, UserId = seed.User.Id, Status = MembershipRequestStatus.Pending,
                Reason = "Pending is not membership", CreatedUtc = FoundationSeed.Now
            });
        if (relation == "Invitation")
            await FoundationSeed.PersistAsync(database, new EventInvitation
            {
                EventId = seed.Event.Id, UserId = seed.User.Id, InvitedById = seed.Other.Id,
                Status = EventInvitationStatus.Pending, CreatedUtc = FoundationSeed.Now, ExpiresUtc = FoundationSeed.Now.AddDays(1)
            });
        await using var db = database.CreateContext();
        var service = new ResourceAccess(StubCurrentUser.For(seed.User));
        await CheckResourceAsync(() => service.RequireEventAsync(db, seed.Event.Id, seed.User.Id), allowed, seed.Event.Id);
        await CheckResourceAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id), allowed, seed.Quest.Id);
        Assert.True(await db.QuestInvitations.AnyAsync(x => x.QuestId == seed.Quest.Id && x.Status == QuestInvitationStatus.Active));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Checks next-call denial after a separate context removes membership, without replacing the service, identity, or tracked context.</summary>
    /// <param name="owner">True to retain a Quest owner grant; false to retain an active Quest invitation.</param>
    /// <returns>A task completing after before/after authorization and durable removed-membership/retained-grant assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipRemoval_DeniesNextCallWithSameServiceAndContext(bool owner)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, visibility: QuestVisibility.Private);
        await GrantAsync(seed, questOwner: owner, invitation: owner ? "None" : "Active");
        var fake = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        Assert.Equal(seed.Event.Id, (await service.RequireEventAsync(db, seed.Event.Id, seed.User.Id)).Id);
        Assert.Equal(seed.Quest.Id, (await service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id)).Id);
        await using (var update = database.CreateContext())
        {
            (await update.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.User.Id)).Status = MembershipStatus.Removed;
            await update.SaveChangesAsync();
        }
        await DeniedAsync(() => service.RequireEventAsync(db, seed.Event.Id, seed.User.Id), ErrorCode.NotFound, Unavailable);
        await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id), ErrorCode.NotFound, Unavailable);
        Assert.Equal(4, fake.Calls);
        await using var read = database.CreateContext();
        Assert.Equal(MembershipStatus.Removed, (await read.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.User.Id)).Status);
        if (owner)
            Assert.Equal(seed.User.Id, (await read.QuestOwners.SingleAsync(x => x.QuestId == seed.Quest.Id)).UserId);
        else
            Assert.Equal(QuestInvitationStatus.Active, (await read.QuestInvitations.SingleAsync(x => x.QuestId == seed.Quest.Id)).Status);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Checks the required next-call membership denial for a Draft Event owner whose owner row remains in SQL.</summary>
    /// <param name="ownerOnly">Whether the second access request requires ownership or ordinary read permission.</param>
    /// <returns>A task completing after same-identity lookup, durable membership removal, and expected safe NotFound assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftEventOwner_MembershipRemoval_DeniesNextCallWithSameIdentity(bool ownerOnly)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, eventStatus: EventStatus.Draft);
        await GrantAsync(seed, eventOwner: true);
        var fake = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        Assert.Equal(seed.Event.Id,
            (await service.RequireEventAsync(db, seed.Event.Id, seed.User.Id, ownerOnly)).Id);
        await using (var update = database.CreateContext())
        {
            (await update.EventMemberships.SingleAsync(
                x => x.EventId == seed.Event.Id && x.UserId == seed.User.Id)).Status = MembershipStatus.Removed;
            await update.SaveChangesAsync();
        }
        var error = await Record.ExceptionAsync(
            () => service.RequireEventAsync(db, seed.Event.Id, seed.User.Id, ownerOnly));
        Assert.Equal(2, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        Assert.Equal(MembershipStatus.Removed, (await read.EventMemberships.SingleAsync(
            x => x.EventId == seed.Event.Id && x.UserId == seed.User.Id)).Status);
        Assert.True(await read.EventOwners.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.User.Id));
        var denial = Assert.IsType<DomainException>(error);
        Assert.Equal(ErrorCode.NotFound, denial.Code);
        Assert.Equal(Unavailable, denial.Message);
        Assert.Null(denial.Field);
    }

    /// <summary>Checks equal Event/Quest owner permissions while creation provenance alone grants neither ownership nor participation.</summary>
    /// <returns>A task completing after both owners' access, creator denial, and relation/count assertions.</returns>
    [Fact]
    public async Task EqualOwnerRows_GiveEqualOwnerOnlyAccess_WithoutCreatorPrivilege()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var secondOwner = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, secondOwner);
        await GrantAsync(seed, eventOwner: true, questOwner: true);
        await FoundationSeed.PersistAsync(database, seed.Membership(secondOwner.Id), seed.Membership(seed.Other.Id),
            new EventOwner { EventId = seed.Event.Id, UserId = secondOwner.Id },
            new QuestOwner { QuestId = seed.Quest.Id, UserId = secondOwner.Id });
        await using var db = database.CreateContext();
        foreach (var user in new[] { seed.User, secondOwner })
        {
            var service = new ResourceAccess(StubCurrentUser.For(user));
            Assert.Equal(seed.Event.Id, (await service.RequireEventAsync(db, seed.Event.Id, user.Id, true)).Id);
            Assert.Equal(seed.Quest.Id, (await service.RequireQuestAsync(db, seed.Quest.Id, user.Id, true)).Id);
        }
        var creator = new ResourceAccess(StubCurrentUser.For(seed.Other));
        await DeniedAsync(() => creator.RequireEventAsync(db, seed.Event.Id, seed.Other.Id, true), ErrorCode.NotFound, Unavailable);
        await DeniedAsync(() => creator.RequireQuestAsync(db, seed.Quest.Id, seed.Other.Id, true), ErrorCode.NotFound, Unavailable);
        Assert.False(await db.Participations.AnyAsync(x => x.QuestId == seed.Quest.Id));
        Assert.Equal(2, await db.EventOwners.CountAsync(x => x.EventId == seed.Event.Id));
        Assert.Equal(2, await db.QuestOwners.CountAsync(x => x.QuestId == seed.Quest.Id));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Checks tenant/object pair binding for two locally eligible tenants without claiming configured-tenant admission behavior.</summary>
    /// <returns>A task completing after identity switching and denial of another tenant account's resource grants.</returns>
    [Fact]
    public async Task EligibleForeignTenantRow_ResolvesOnlyItsOwnPair()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var foreign = FoundationSeed.NewUser();
        foreign.ObjectId = seed.User.ObjectId;
        await FoundationSeed.PersistAsync(database, foreign);
        await GrantAsync(seed, eventOwner: true);
        var fake = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        Assert.Equal(seed.User.Id, (await service.RequireUserAsync(db)).Id);
        Assert.Equal(seed.Event.Id, (await service.RequireEventAsync(db, seed.Event.Id, seed.User.Id, true)).Id);
        fake.Identity = StubCurrentUser.For(foreign).Identity;
        Assert.Equal(foreign.Id, (await service.RequireUserAsync(db)).Id);
        await DeniedAsync(() => service.RequireEventAsync(db, seed.Event.Id, foreign.Id), ErrorCode.NotFound, Unavailable);
        await DeniedAsync(() => service.RequireEventAsync(db, seed.Event.Id, seed.User.Id, true), ErrorCode.NotFound, Unavailable);
        Assert.Equal(5, fake.Calls);
        Assert.Equal(2, await db.Users.CountAsync(x => x.ObjectId == seed.User.ObjectId));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private async Task GrantAsync(FoundationSeed seed, string membership = "Active", bool eventOwner = false,
        bool questOwner = false, string invitation = "None")
    {
        var rows = new List<Entity>();
        if (membership != "None")
            rows.Add(seed.Membership(status: membership == "Removed" ? MembershipStatus.Removed : MembershipStatus.Active));
        if (eventOwner)
            rows.Add(new EventOwner { EventId = seed.Event.Id, UserId = seed.User.Id });
        if (questOwner)
            rows.Add(new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id });
        if (invitation != "None")
            rows.Add(seed.Invitation(invitation == "Revoked" ? QuestInvitationStatus.Revoked : QuestInvitationStatus.Active));
        await FoundationSeed.PersistAsync(database, rows.ToArray());
    }

    private async Task ConfigureAsync(FoundationSeed seed, QuestStatus status = QuestStatus.Active,
        QuestVisibility visibility = QuestVisibility.Public, EventStatus eventStatus = EventStatus.Active)
    {
        await using var db = database.CreateContext();
        var quest = await db.Quests.SingleAsync(x => x.Id == seed.Quest.Id);
        quest.Status = status;
        quest.Visibility = visibility;
        (await db.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status = eventStatus;
        await db.SaveChangesAsync();
    }

    private async Task SetEligibilityAsync(Guid id, bool eligible)
    {
        await using var db = database.CreateContext();
        (await db.Users.SingleAsync(x => x.Id == id)).IsEligible = eligible;
        await db.SaveChangesAsync();
    }

    private static async Task CheckResourceAsync<T>(Func<Task<T>> act, bool allowed, Guid id) where T : Entity
    {
        if (allowed)
        {
            var result = await act();
            Assert.Equal(id, result.Id);
            Assert.Equal(8, result.Version.Length);
        }
        else
            await DeniedAsync(async () => await act(), ErrorCode.NotFound, Unavailable);
    }

    private static async Task<DomainException> DeniedAsync(Func<Task> act, ErrorCode code, string message)
    {
        var error = await Assert.ThrowsAsync<DomainException>(act);
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
        Assert.Null(error.Field);
        return error;
    }

    private static void AssertSafe(DomainException error, FoundationSeed seed, Guid requested)
    {
        foreach (var secret in new[]
        {
            seed.Event.Name, seed.Event.Description, seed.Quest.Title, seed.Quest.Description,
            seed.Quest.Location, seed.User.Id.ToString(), seed.Other.Id.ToString(), requested.ToString()
        })
        {
            Assert.DoesNotContain(secret, error.Message);
            Assert.DoesNotContain(secret, error.ToString());
        }
    }

    private static async Task<Entity> AsEntity<T>(Task<T> task) where T : Entity => await task;

    /// <summary>Checks that an authorized invited member retains historical Quest read access under every non-Draft parent state.</summary>
    /// <param name="parentStatus">The Active, Completed, Cancelled, or Archived Event state.</param>
    /// <returns>A task completing after exact Quest identity/status, parent status, and no-write assertions.</returns>
    [Theory]
    [InlineData(EventStatus.Active)]
    [InlineData(EventStatus.Completed)]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task RequireQuest_NonDraftParent_PreservesAuthorizedHistoricalRead(EventStatus parentStatus)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, QuestStatus.Completed, QuestVisibility.Private, parentStatus);
        await GrantAsync(seed, invitation: "Active");
        var service = new ResourceAccess(StubCurrentUser.For(seed.User));
        await using var db = database.CreateContext();
        var quest = await service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id);
        Assert.Equal(seed.Quest.Id, quest.Id);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.Equal(parentStatus, (await db.Events.SingleAsync(x => x.Id == quest.EventId)).Status);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Checks privacy-safe private Quest denial even when the caller can read its parent Event.</summary>
    /// <returns>A task completing after allowed parent access, denied child content, and absent invitation/no-write assertions.</returns>
    [Fact]
    public async Task UnavailablePrivateQuest_ActiveParentMember_ReceivesNoSensitiveContent()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await ConfigureAsync(seed, visibility: QuestVisibility.Private);
        await GrantAsync(seed);
        var service = new ResourceAccess(StubCurrentUser.For(seed.User));
        await using var db = database.CreateContext();
        Assert.Equal(seed.Event.Id, (await service.RequireEventAsync(db, seed.Event.Id, seed.User.Id)).Id);
        AssertSafe(await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id),
            ErrorCode.NotFound, Unavailable), seed, seed.Quest.Id);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.False(await db.QuestInvitations.AnyAsync(x => x.QuestId == seed.Quest.Id));
    }

    /// <summary>Checks next-call verified-departure denial even when eligibility and all ownership/admin grants remain true.</summary>
    /// <returns>A task completing after every access route is denied on the same identity/service/context and persisted grants remain intact.</returns>
    [Fact]
    public async Task VerifiedDeparture_EligibleOwnerAndAdministrator_DeniesAllRoutesOnSameInstance()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await GrantAsync(seed, eventOwner: true, questOwner: true, invitation: "Active");
        await FoundationSeed.PersistAsync(database, new Administrator { UserId = seed.User.Id });
        var fake = StubCurrentUser.For(seed.User);
        var service = new ResourceAccess(fake);
        await using var db = database.CreateContext();
        Assert.Equal(seed.User.Id, (await service.RequireUserAsync(db)).Id);
        Assert.Equal(seed.User.Id, (await service.RequireAdministratorAsync(db)).Id);
        Assert.Equal(seed.Event.Id, (await service.RequireEventAsync(db, seed.Event.Id, seed.User.Id, true)).Id);
        Assert.Equal(seed.Quest.Id, (await service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, true, true)).Id);
        await using (var update = database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == seed.User.Id)).DepartureVerifiedUtc = FoundationSeed.Now;
            Assert.Equal(1, await update.SaveChangesAsync());
        }
        await DeniedAsync(() => service.RequireUserAsync(db), ErrorCode.Forbidden, Ineligible);
        await DeniedAsync(() => service.RequireAdministratorAsync(db), ErrorCode.Forbidden, Ineligible);
        foreach (var ownerOnly in new[] { false, true })
        {
            await DeniedAsync(() => service.RequireEventAsync(db, seed.Event.Id, seed.User.Id, ownerOnly), ErrorCode.Forbidden, Ineligible);
            foreach (var moderation in new[] { false, true })
                await DeniedAsync(() => service.RequireQuestAsync(db, seed.Quest.Id, seed.User.Id, ownerOnly, moderation), ErrorCode.Forbidden, Ineligible);
        }
        Assert.Equal(12, fake.Calls);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var read = database.CreateContext();
        var departed = await read.Users.SingleAsync(x => x.Id == seed.User.Id);
        Assert.True(departed.IsEligible);
        Assert.Equal(FoundationSeed.Now, departed.DepartureVerifiedUtc);
        Assert.True(await read.Administrators.AnyAsync(x => x.UserId == seed.User.Id));
        Assert.True(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id &&
            x.UserId == seed.User.Id && x.Status == MembershipStatus.Active));
        Assert.True(await read.EventOwners.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.User.Id));
        Assert.True(await read.QuestOwners.AnyAsync(x => x.QuestId == seed.Quest.Id && x.UserId == seed.User.Id));
    }
}
