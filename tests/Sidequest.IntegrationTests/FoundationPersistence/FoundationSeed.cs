using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Holds independent persisted principals and aggregates for one SQL scenario without implicit access grants.</summary>
/// <param name="User">The eligible account used as the scenario's current caller.</param>
/// <param name="Other">The distinct creator account, which receives no implicit owner relation.</param>
/// <param name="Event">The persisted Active parent with Prague civil dates.</param>
/// <param name="Quest">The persisted Active public Quest beneath that parent.</param>
/// <remarks>The contained EF entities are mutable and belong to one scenario; do not share them or their contexts between concurrent operations.</remarks>
internal sealed record FoundationSeed(UserAccount User, UserAccount Other, Event Event, Quest Quest)
{
    /// <summary>The fixed UTC instant used for deterministic seed timestamps, rather than the machine clock.</summary>
    public static readonly DateTimeOffset Now = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
    /// <summary>The exact safe domain message expected when SQL detects a duplicate persisted key.</summary>
    public const string DuplicateMessage = "This operation conflicts with a change already saved. Reload and try again.";
    /// <summary>The exact safe domain message expected when a stale rowversion loses a write race.</summary>
    public const string ConcurrencyMessage = "This item changed. Reload and try again.";

    /// <summary>Creates an unsaved eligible synthetic account with a unique tenant/object pair and fixed contact values.</summary>
    /// <returns>A new untracked account without a database-generated rowversion.</returns>
    public static UserAccount NewUser() => new()
    {
        TenantId = Guid.NewGuid(), ObjectId = Guid.NewGuid(), IsEligible = true,
        DisplayName = "Synthetic Ada", Email = "ada@example.invalid"
    };

    /// <summary>Creates an unsaved Active Event with sensitive sentinels and fixed inclusive July 2026 Prague dates.</summary>
    /// <param name="creator">The ID of an existing creator account; this does not grant ownership or membership.</param>
    /// <returns>A new untracked Event with no attached owner or membership rows.</returns>
    public static Event NewEvent(Guid creator) => new()
    {
        CreatorId = creator, Name = "Sensitive Event sentinel", Description = "Private event details",
        DiscoverySummary = "Visible discovery summary", StartDate = new(2026, 7, 15),
        EndDate = new(2026, 7, 16), TimeZoneId = "Europe/Prague", Status = EventStatus.Active,
        CreatedUtc = Now, UpdatedUtc = Now
    };

    /// <summary>Creates an unsaved Active public Quest with a fixed two-hour UTC interval and sensitive content sentinels.</summary>
    /// <param name="parent">The ID of the existing parent Event.</param>
    /// <param name="creator">The ID of the existing creator, without an implicit Quest ownership grant.</param>
    /// <returns>A new untracked Quest with calendar revision seven and no participation rows.</returns>
    public static Quest NewQuest(Guid parent, Guid creator) => new()
    {
        EventId = parent, CreatorId = creator, Title = "Sensitive Quest sentinel",
        Description = "Private quest details", Location = "Private room", Status = QuestStatus.Active,
        Visibility = QuestVisibility.Public, StartUtc = Now, EndUtc = Now.AddHours(2),
        CreatedUtc = Now, UpdatedUtc = Now, CalendarRevision = 7
    };

    /// <summary>Persists two accounts, their Event, and its Quest in dependency order within the owned database.</summary>
    /// <param name="database">The initialized isolated SQL fixture in which to insert this scenario's unique rows.</param>
    /// <returns>The inserted rows, including their assigned identifiers and SQL-generated rowversions.</returns>
    /// <example>
    /// <code>
    /// var seed = await FoundationSeed.CreateAsync(database);
    /// await FoundationSeed.PersistAsync(database, seed.Membership());
    /// await using var read = database.CreateContext();
    /// var member = await read.EventMemberships.SingleAsync(x => x.EventId == seed.Event.Id);
    /// Assert.Equal(MembershipStatus.Active, member.Status);
    /// </code>
    /// </example>
    public static async Task<FoundationSeed> CreateAsync(SqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        var user = NewUser();
        var other = NewUser();
        db.Users.AddRange(user, other);
        await db.SaveChangesAsync();
        var parent = NewEvent(other.Id);
        db.Events.Add(parent);
        await db.SaveChangesAsync();
        var quest = NewQuest(parent.Id, other.Id);
        db.Quests.Add(quest);
        await db.SaveChangesAsync();
        return new(user, other, parent, quest);
    }

    /// <summary>Creates an unsaved individual membership for this seed's Event without changing existing rows.</summary>
    /// <param name="user">The member account ID, or null to use the seed's current caller.</param>
    /// <param name="status">The explicit Active or Removed membership state.</param>
    /// <returns>An untracked relation with a fixed change timestamp and the other account as actor.</returns>
    public EventMembership Membership(Guid? user = null, MembershipStatus status = MembershipStatus.Active) => new()
    {
        EventId = Event.Id, UserId = user ?? User.Id, Status = status,
        ChangedById = Other.Id, ChangedUtc = Now
    };

    /// <summary>Creates an unsaved identity-bound invitation for the seed's caller and Quest.</summary>
    /// <param name="status">The Active or Revoked invitation state.</param>
    /// <returns>An untracked invitation grant; it does not create membership or participation.</returns>
    public QuestInvitation Invitation(QuestInvitationStatus status = QuestInvitationStatus.Active) => new()
    {
        QuestId = Quest.Id, UserId = User.Id, InvitedById = Other.Id, Status = status, ChangedUtc = Now
    };

    /// <summary>Asserts the exact DomainException type, Conflict code, safe message, and absent field.</summary>
    /// <param name="error">The captured save exception; null intentionally fails the expected-conflict assertion.</param>
    /// <param name="message">The exact concurrency, duplicate-key, or history-immutability message required.</param>
    public static void AssertConflict(Exception? error, string message)
    {
        var domain = Assert.IsType<DomainException>(error);
        Assert.Equal(ErrorCode.Conflict, domain.Code);
        Assert.Null(domain.Field);
        Assert.Equal(message, domain.Message);
    }

    /// <summary>Inserts supplied rows through the production asynchronous save contract using a disposable context.</summary>
    /// <param name="database">The initialized SQL fixture that exclusively owns the target catalog.</param>
    /// <param name="rows">The new entities to persist together; generated rowversions are populated on these instances.</param>
    /// <returns>A task that completes after the save and context disposal.</returns>
    /// <remarks>Writes only the supplied rows; it does not reset shared fixture state or retry a rejected operation. Await completion before reading generated values on the same entity instances.</remarks>
    public static async Task PersistAsync(SqlTestDatabase database, params Entity[] rows)
    {
        await using var db = database.CreateContext();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }
}
