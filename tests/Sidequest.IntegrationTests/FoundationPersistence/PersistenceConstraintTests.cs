using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Domain.Model;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks real SQL uniqueness, filtered keys, participation/interval checks, and restrictive foreign keys.</summary>
/// <param name="database">The class-owned migrated catalog; all scenarios insert independently identified rows.</param>
public sealed class PersistenceConstraintTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Checks composite tenant/object uniqueness while allowing either key component to differ independently.</summary>
    /// <returns>A task completing after duplicate Conflict and original-account content/version assertions.</returns>
    [Fact]
    public async Task Users_TenantObjectPair_IsUnique()
    {
        var original = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, original);
        var duplicate = FoundationSeed.NewUser();
        duplicate.TenantId = original.TenantId;
        duplicate.ObjectId = original.ObjectId;
        duplicate.Email = "replacement@example.invalid";
        await RejectDuplicateAsync(duplicate);
        var otherTenant = FoundationSeed.NewUser();
        otherTenant.ObjectId = original.ObjectId;
        var otherObject = FoundationSeed.NewUser();
        otherObject.TenantId = original.TenantId;
        await FoundationSeed.PersistAsync(database, otherTenant, otherObject);
        await using var read = database.CreateContext();
        var stored = await read.Users.SingleAsync(x => x.TenantId == original.TenantId && x.ObjectId == original.ObjectId);
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal("ada@example.invalid", stored.Email);
        Assert.Equal("Synthetic Ada", stored.DisplayName);
        Assert.Equal(original.Version, stored.Version);
        Assert.Equal(3, await read.Users.CountAsync(x => x.TenantId == original.TenantId || x.ObjectId == original.ObjectId));
    }

    /// <summary>Checks Event/user membership uniqueness across Active and Removed states without rejecting different pairs.</summary>
    /// <param name="first">The original persisted membership status.</param>
    /// <param name="second">The conflicting attempted membership status.</param>
    /// <returns>A task completing after failed duplicate insertion and original/noncolliding relation assertions.</returns>
    [Theory]
    [InlineData(MembershipStatus.Active, MembershipStatus.Active)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Removed)]
    [InlineData(MembershipStatus.Removed, MembershipStatus.Removed)]
    [InlineData(MembershipStatus.Removed, MembershipStatus.Active)]
    public async Task EventMemberships_EventUserPair_IsUniqueAcrossStatuses(MembershipStatus first, MembershipStatus second)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var other = await FoundationSeed.CreateAsync(database);
        await CheckPairAsync("EventId", seed.Event.Id, other.Event.Id, seed,
            (aggregate, user, duplicate) => new EventMembership
            {
                EventId = aggregate, UserId = user, Status = duplicate ? second : first,
                ChangedById = seed.Other.Id, ChangedUtc = FoundationSeed.Now
            }, row => Assert.Equal(first, row.Status));
    }

    /// <summary>Checks equal owner rows on an aggregate while rejecting repeated ownership of the same aggregate/user pair.</summary>
    /// <param name="quest">True to exercise Quest owners; false to exercise Event owners.</param>
    /// <returns>A task completing after distinct-owner and duplicate-pair persistence assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerRelations_AllowEqualOwners_RejectDuplicatePair(bool quest)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var other = await FoundationSeed.CreateAsync(database);
        if (quest)
            await CheckPairAsync("QuestId", seed.Quest.Id, other.Quest.Id, seed,
                (aggregate, user, _) => new QuestOwner { QuestId = aggregate, UserId = user },
                row => Assert.Equal(seed.User.Id, row.UserId));
        else
            await CheckPairAsync("EventId", seed.Event.Id, other.Event.Id, seed,
                (aggregate, user, _) => new EventOwner { EventId = aggregate, UserId = user },
                row => Assert.Equal(seed.User.Id, row.UserId));
    }

    /// <summary>Checks Quest/user participation uniqueness for every ordered pair of valid exclusive states.</summary>
    /// <param name="first">The original persisted participation state.</param>
    /// <param name="second">The duplicate row's attempted participation state.</param>
    /// <returns>A task completing after duplicate Conflict and original/noncolliding participation assertions.</returns>
    [Theory]
    [InlineData(ParticipationStatus.None, ParticipationStatus.None)]
    [InlineData(ParticipationStatus.None, ParticipationStatus.Following)]
    [InlineData(ParticipationStatus.None, ParticipationStatus.Joined)]
    [InlineData(ParticipationStatus.Following, ParticipationStatus.None)]
    [InlineData(ParticipationStatus.Following, ParticipationStatus.Following)]
    [InlineData(ParticipationStatus.Following, ParticipationStatus.Joined)]
    [InlineData(ParticipationStatus.Joined, ParticipationStatus.None)]
    [InlineData(ParticipationStatus.Joined, ParticipationStatus.Following)]
    [InlineData(ParticipationStatus.Joined, ParticipationStatus.Joined)]
    public async Task QuestParticipations_QuestUserPair_IsUniqueAcrossStates(ParticipationStatus first, ParticipationStatus second)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var other = await FoundationSeed.CreateAsync(database);
        await CheckPairAsync("QuestId", seed.Quest.Id, other.Quest.Id, seed,
            (aggregate, user, duplicate) => new QuestParticipation
            {
                QuestId = aggregate, UserId = user, Status = duplicate ? second : first,
                ChangedUtc = FoundationSeed.Now
            }, row => Assert.Equal(first, row.Status));
    }

    /// <summary>Checks that a revoked Quest grant does not free its Quest/user uniqueness key.</summary>
    /// <param name="first">The original Active or Revoked invitation status.</param>
    /// <param name="second">The conflicting attempted invitation status.</param>
    /// <returns>A task completing after rejected duplicate and preserved invitation assertions.</returns>
    [Theory]
    [InlineData(QuestInvitationStatus.Active, QuestInvitationStatus.Active)]
    [InlineData(QuestInvitationStatus.Active, QuestInvitationStatus.Revoked)]
    [InlineData(QuestInvitationStatus.Revoked, QuestInvitationStatus.Revoked)]
    [InlineData(QuestInvitationStatus.Revoked, QuestInvitationStatus.Active)]
    public async Task QuestInvitations_QuestUserPair_IsUniqueEvenWhenRevoked(QuestInvitationStatus first, QuestInvitationStatus second)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var other = await FoundationSeed.CreateAsync(database);
        await CheckPairAsync("QuestId", seed.Quest.Id, other.Quest.Id, seed,
            (aggregate, user, duplicate) => new QuestInvitation
            {
                QuestId = aggregate, UserId = user, Status = duplicate ? second : first,
                InvitedById = seed.Other.Id, ChangedUtc = FoundationSeed.Now
            }, row => Assert.Equal(first, row.Status));
    }

    /// <summary>Checks that only one pending Event relation of each kind exists for a given Event/user pair.</summary>
    /// <param name="invitation">True for Event invitations; false for membership requests.</param>
    /// <returns>A task completing after pending-key Conflict and independent-key control assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingEventRelations_RejectSecondPending(bool invitation)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var other = await FoundationSeed.CreateAsync(database);
        if (invitation)
            await CheckPairAsync("EventId", seed.Event.Id, other.Event.Id, seed,
                (aggregate, user, _) => Invite(seed, aggregate, user, EventInvitationStatus.Pending),
                row => Assert.Equal(EventInvitationStatus.Pending, row.Status));
        else
            await CheckPairAsync("EventId", seed.Event.Id, other.Event.Id, seed,
                (aggregate, user, _) => Request(aggregate, user, MembershipRequestStatus.Pending),
                row => Assert.Equal(MembershipRequestStatus.Pending, row.Status));
    }

    /// <summary>Checks terminal-history coexistence, blocked terminal-to-pending collision, and reuse of a freed pending slot.</summary>
    /// <param name="invitation">True for Event invitations; false for membership requests.</param>
    /// <returns>A task completing after all terminal partitions and pending lifecycle persistence assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingEventRelations_TerminalHistoryAndOnePending_Coexist(bool invitation)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        // Every documented terminal state is retained, including repeated terminal history.
        if (invitation)
            await CheckPendingLifecycleAsync(seed,
                new[] { EventInvitationStatus.Accepted, EventInvitationStatus.Declined, EventInvitationStatus.Revoked, EventInvitationStatus.Expired },
                state => Invite(seed, seed.Event.Id, seed.User.Id, state), EventInvitationStatus.Pending,
                (row, state) => row.Status = state, row => row.Status);
        else
            await CheckPendingLifecycleAsync(seed,
                new[] { MembershipRequestStatus.Approved, MembershipRequestStatus.Rejected, MembershipRequestStatus.Withdrawn },
                state => Request(seed.Event.Id, seed.User.Id, state), MembershipRequestStatus.Pending,
                (row, state) => row.Status = state, row => row.Status);
    }

    /// <summary>Checks source-change/user/kind deduplication without replacing the original access-loss notice.</summary>
    /// <returns>A task completing after each independent key-component control and retained notification-content assertion.</returns>
    [Fact]
    public async Task Notifications_DeduplicateSourceUserKind()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var source = Guid.NewGuid();
        Notification New(Guid change, Guid user, NotificationKind kind) => new()
        {
            SourceChangeId = change, UserId = user, Kind = kind, EventId = seed.Event.Id,
            QuestId = seed.Quest.Id, Summary = "Original access-loss notice", IsAccessLossNotice = true,
            CreatedUtc = FoundationSeed.Now
        };
        var original = New(source, seed.User.Id, NotificationKind.AccessRemoved);
        await FoundationSeed.PersistAsync(database, original);
        var duplicate = New(source, seed.User.Id, NotificationKind.AccessRemoved);
        duplicate.Summary = "Replacement must not persist";
        await RejectDuplicateAsync(duplicate);
        var siblings = new[]
        {
            New(Guid.NewGuid(), seed.User.Id, NotificationKind.AccessRemoved),
            New(source, seed.Other.Id, NotificationKind.AccessRemoved),
            New(source, seed.User.Id, NotificationKind.Joined)
        };
        await FoundationSeed.PersistAsync(database, siblings);
        await using var db = database.CreateContext();
        var stored = await db.Notifications.SingleAsync(x =>
            x.SourceChangeId == source && x.UserId == seed.User.Id && x.Kind == NotificationKind.AccessRemoved);
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal("Original access-loss notice", stored.Summary);
        Assert.True(stored.IsAccessLossNotice);
        Assert.Equal(seed.Event.Id, stored.EventId);
        Assert.Equal(seed.Quest.Id, stored.QuestId);
        Assert.Equal(FoundationSeed.Now, stored.CreatedUtc);
        Assert.Null(stored.ReadUtc);
        Assert.Equal(original.Version, stored.Version);
        foreach (var sibling in siblings)
            Assert.Equal(sibling.Id, (await db.Notifications.SingleAsync(x =>
                x.SourceChangeId == sibling.SourceChangeId && x.UserId == sibling.UserId && x.Kind == sibling.Kind)).Id);
        Assert.Equal(4, await db.Notifications.CountAsync(x => x.QuestId == seed.Quest.Id));
    }

    /// <summary>Checks SQL check-constraint rejection of undefined participation values and absence of the invalid row.</summary>
    /// <param name="invalid">An integer outside the three allowed stored participation states.</param>
    /// <returns>A task completing after exact SQL error-number and unchanged-parent assertions.</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task ParticipationStatus_CheckConstraint_RejectsInvalidValues(int invalid)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var row = new QuestParticipation
        {
            QuestId = seed.Quest.Id, UserId = seed.User.Id, Status = (ParticipationStatus)invalid,
            ChangedUtc = FoundationSeed.Now
        };
        await RejectCheckAsync(row);
        await using var db = database.CreateContext();
        Assert.False(await db.Participations.AnyAsync(x => x.QuestId == seed.Quest.Id));
        Assert.Equal(QuestStatus.Active, (await db.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
    }

    /// <summary>Checks that every allowed exclusive participation state persists with the correct user and generated rowversion.</summary>
    /// <param name="status">The None, Following, or Joined state to store.</param>
    /// <returns>A task completing after fresh-context state and eight-byte rowversion assertions.</returns>
    [Theory]
    [InlineData(ParticipationStatus.None)]
    [InlineData(ParticipationStatus.Following)]
    [InlineData(ParticipationStatus.Joined)]
    public async Task ParticipationStatus_ValidStates_PersistExactly(ParticipationStatus status)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var row = new QuestParticipation { QuestId = seed.Quest.Id, UserId = seed.User.Id, Status = status, ChangedUtc = FoundationSeed.Now };
        await FoundationSeed.PersistAsync(database, row);
        await using var db = database.CreateContext();
        var stored = await db.Participations.SingleAsync(x => x.QuestId == seed.Quest.Id);
        Assert.Equal(status, stored.Status);
        Assert.Equal(seed.User.Id, stored.UserId);
        Assert.Equal(8, stored.Version.Length);
    }

    /// <summary>Checks controlled Following-to-Joined-to-None SQL updates without duplicate rows or incorrect neighboring counts.</summary>
    /// <returns>A task completing after fresh participant counts and exclusive-state assertions at each update.</returns>
    [Fact]
    public async Task ParticipationUpdate_StoresOneExclusiveStateAndCorrectCounts()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var row = new QuestParticipation { QuestId = seed.Quest.Id, UserId = seed.User.Id, Status = ParticipationStatus.Following, ChangedUtc = FoundationSeed.Now };
        await FoundationSeed.PersistAsync(database, row, new QuestParticipation
        {
            QuestId = seed.Quest.Id, UserId = seed.Other.Id, Status = ParticipationStatus.Joined, ChangedUtc = FoundationSeed.Now
        });
        await CheckCounts(1, 1, ParticipationStatus.Following);
        await Update(ParticipationStatus.Joined);
        await CheckCounts(0, 2, ParticipationStatus.Joined);
        await Update(ParticipationStatus.None);
        await CheckCounts(0, 1, ParticipationStatus.None);

        async Task Update(ParticipationStatus status)
        {
            await using var db = database.CreateContext();
            (await db.Participations.SingleAsync(x => x.Id == row.Id)).Status = status;
            Assert.Equal(1, await db.SaveChangesAsync());
        }
        async Task CheckCounts(int following, int joined, ParticipationStatus status)
        {
            await using var db = database.CreateContext();
            var rows = await db.Participations.Where(x => x.QuestId == seed.Quest.Id).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(following, rows.Count(x => x.Status == ParticipationStatus.Following));
            Assert.Equal(joined, rows.Count(x => x.Status == ParticipationStatus.Joined));
            Assert.Equal(status, Assert.Single(rows, x => x.UserId == seed.User.Id).Status);
        }
    }

    /// <summary>Checks SQL's strictly positive Quest interval at equal, negative-one-tick, and valid duration boundaries.</summary>
    /// <param name="ticks">The requested end-minus-start duration in 100-nanosecond ticks.</param>
    /// <param name="allowed">Whether the interval must persist rather than fail the SQL check.</param>
    /// <returns>A task completing after exact duration/error and unchanged neighboring-Quest assertions.</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(72000000000L, true)]
    public async Task QuestInterval_CheckConstraint_RequiresStrictPositiveDuration(long ticks, bool allowed)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var row = FoundationSeed.NewQuest(seed.Event.Id, seed.User.Id);
        row.EndUtc = row.StartUtc.AddTicks(ticks);
        if (allowed)
            await FoundationSeed.PersistAsync(database, row);
        else
            await RejectCheckAsync(row);
        await using var db = database.CreateContext();
        var stored = await db.Quests.SingleOrDefaultAsync(x => x.Id == row.Id);
        if (allowed)
        {
            Assert.NotNull(stored);
            Assert.Equal(TimeSpan.FromTicks(ticks), stored.EndUtc - stored.StartUtc);
            Assert.Equal(8, stored.Version.Length);
        }
        else
            Assert.Null(stored);
        Assert.Equal(seed.Quest.Version, (await db.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Version);
    }

    private async Task CheckPairAsync<T>(string key, Guid aggregate, Guid otherAggregate, FoundationSeed seed,
        Func<Guid, Guid, bool, T> create, Action<T> assertOriginal) where T : Entity
    {
        var original = create(aggregate, seed.User.Id, false);
        await FoundationSeed.PersistAsync(database, original);
        var duplicate = create(aggregate, seed.User.Id, true);
        Assert.NotEqual(original.Id, duplicate.Id);
        await RejectDuplicateAsync(duplicate);
        var otherUser = create(aggregate, seed.Other.Id, false);
        var otherParent = create(otherAggregate, seed.User.Id, false);
        await FoundationSeed.PersistAsync(database, otherUser, otherParent);
        await using var db = database.CreateContext();
        var stored = await db.Set<T>().SingleAsync(x =>
            EF.Property<Guid>(x, key) == aggregate && EF.Property<Guid>(x, "UserId") == seed.User.Id);
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal(original.Version, stored.Version);
        assertOriginal(stored);
        Assert.Equal(2, await db.Set<T>().CountAsync(x => EF.Property<Guid>(x, key) == aggregate));
        Assert.Equal(otherUser.Id, (await db.Set<T>().SingleAsync(x => x.Id == otherUser.Id)).Id);
        Assert.Equal(otherParent.Id, (await db.Set<T>().SingleAsync(x => x.Id == otherParent.Id)).Id);
        Assert.False(await db.Set<T>().AnyAsync(x => x.Id == duplicate.Id));
    }

    private async Task CheckPendingLifecycleAsync<T, TStatus>(FoundationSeed seed, TStatus[] terminalStates,
        Func<TStatus, T> create, TStatus pending, Action<T, TStatus> setStatus, Func<T, TStatus> getStatus)
        where T : Entity where TStatus : struct, Enum
    {
        var history = terminalStates.Select(create).Append(create(terminalStates[0])).ToArray();
        var active = create(pending);
        await FoundationSeed.PersistAsync(database, history.Cast<Entity>().Append(active).ToArray());
        await RejectDuplicateAsync(create(pending));
        await using (var failure = database.CreateContext())
        {
            var terminal = await failure.Set<T>().SingleAsync(x => x.Id == history[0].Id);
            setStatus(terminal, pending);
            FoundationSeed.AssertConflict(await Record.ExceptionAsync(() => failure.SaveChangesAsync()), FoundationSeed.DuplicateMessage);
        }
        await using (var read = database.CreateContext())
        {
            var rows = await read.Set<T>().Where(x => EF.Property<Guid>(x, "EventId") == seed.Event.Id).ToListAsync();
            Assert.Equal(terminalStates.Length + 2, rows.Count);
            Assert.Equal(pending, getStatus(Assert.Single(rows, x => x.Id == active.Id)));
            foreach (var original in history)
            {
                var stored = Assert.Single(rows, x => x.Id == original.Id);
                Assert.Equal(getStatus(original), getStatus(stored));
                Assert.Equal(original.Version, stored.Version);
            }
        }
        await using (var update = database.CreateContext())
        {
            setStatus(await update.Set<T>().SingleAsync(x => x.Id == active.Id), terminalStates[0]);
            await update.SaveChangesAsync();
        }
        var replacement = create(pending);
        await FoundationSeed.PersistAsync(database, replacement);
        await using var final = database.CreateContext();
        var all = await final.Set<T>().Where(x => EF.Property<Guid>(x, "EventId") == seed.Event.Id).ToListAsync();
        Assert.Equal(terminalStates.Length + 3, all.Count);
        Assert.Equal(replacement.Id, Assert.Single(all, x => EqualityComparer<TStatus>.Default.Equals(getStatus(x), pending)).Id);
        Assert.Equal(terminalStates[0], getStatus(Assert.Single(all, x => x.Id == active.Id)));
    }

    private async Task RejectDuplicateAsync(Entity row)
    {
        await using var db = database.CreateContext();
        db.Add(row);
        FoundationSeed.AssertConflict(await Record.ExceptionAsync(() => db.SaveChangesAsync()), FoundationSeed.DuplicateMessage);
    }

    /// <summary>Checks orphan insertion and parent-deletion rejection while preserving existing related rows and rowversions.</summary>
    /// <param name="principal">The User, Event, or Quest foreign-key principal to exercise.</param>
    /// <param name="delete">True to delete a referenced parent; false to insert a relation to a nonexistent parent.</param>
    /// <returns>A task completing after SQL error 547 and fresh retained-state assertions.</returns>
    [Theory]
    [InlineData("User", false)]
    [InlineData("User", true)]
    [InlineData("Event", false)]
    [InlineData("Event", true)]
    [InlineData("Quest", false)]
    [InlineData("Quest", true)]
    public async Task RestrictForeignKeys_RejectOrphansAndParentDeletion_PreserveRelatedRows(string principal, bool delete)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var owner = new EventOwner { EventId = seed.Event.Id, UserId = seed.User.Id };
        var participation = new QuestParticipation
        {
            QuestId = seed.Quest.Id, UserId = seed.User.Id,
            Status = ParticipationStatus.Joined, ChangedUtc = FoundationSeed.Now
        };
        await FoundationSeed.PersistAsync(database, owner, participation);
        var invalidId = Guid.NewGuid();
        await using (var failure = database.CreateContext())
        {
            if (delete)
            {
                Entity target = principal switch
                {
                    "User" => await failure.Users.SingleAsync(x => x.Id == seed.User.Id),
                    "Event" => await failure.Events.SingleAsync(x => x.Id == seed.Event.Id),
                    "Quest" => await failure.Quests.SingleAsync(x => x.Id == seed.Quest.Id),
                    _ => throw new ArgumentOutOfRangeException(nameof(principal))
                };
                failure.Remove(target);
            }
            else
            {
                Entity orphan = principal switch
                {
                    "User" => new EventOwner { EventId = seed.Event.Id, UserId = invalidId },
                    "Event" => new EventOwner { EventId = invalidId, UserId = seed.User.Id },
                    "Quest" => new QuestParticipation
                    {
                        QuestId = invalidId, UserId = seed.User.Id,
                        Status = ParticipationStatus.Joined, ChangedUtc = FoundationSeed.Now
                    },
                    _ => throw new ArgumentOutOfRangeException(nameof(principal))
                };
                failure.Add(orphan);
            }
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => failure.SaveChangesAsync());
            Assert.Equal(547, Assert.IsType<SqlException>(error.InnerException).Number);
        }
        await using var read = database.CreateContext();
        Assert.Equal("Synthetic Ada", (await read.Users.SingleAsync(x => x.Id == seed.User.Id)).DisplayName);
        Assert.Equal(seed.Event.Version, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Version);
        Assert.Equal(seed.Quest.Version, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Version);
        Assert.Equal(owner.Version, (await read.EventOwners.SingleAsync(x => x.Id == owner.Id)).Version);
        var stored = await read.Participations.SingleAsync(x => x.QuestId == seed.Quest.Id);
        Assert.Equal(ParticipationStatus.Joined, stored.Status);
        Assert.Equal(participation.Version, stored.Version);
        Assert.False(await read.EventOwners.AnyAsync(x => x.UserId == invalidId || x.EventId == invalidId));
        Assert.False(await read.Participations.AnyAsync(x => x.QuestId == invalidId));
    }

    private async Task RejectCheckAsync(Entity row)
    {
        await using var db = database.CreateContext();
        db.Add(row);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(547, Assert.IsType<SqlException>(error.InnerException).Number);
    }

    private static EventMembershipRequest Request(Guid parent, Guid user, MembershipRequestStatus status) => new()
    {
        EventId = parent, UserId = user, Status = status, Reason = "Please include me", CreatedUtc = FoundationSeed.Now,
        DecidedUtc = status == MembershipRequestStatus.Pending ? null : FoundationSeed.Now.AddHours(1)
    };

    private static EventInvitation Invite(FoundationSeed seed, Guid parent, Guid user, EventInvitationStatus status) => new()
    {
        EventId = parent, UserId = user, InvitedById = seed.Other.Id, Status = status,
        CreatedUtc = FoundationSeed.Now, ExpiresUtc = FoundationSeed.Now.AddDays(2),
        ResolvedUtc = status == EventInvitationStatus.Pending ? null : FoundationSeed.Now.AddHours(1)
    };
}
