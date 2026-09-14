using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks the deployed SQL migration, ownership/time-zone representation, rowversion metadata, and isolated database lifetime.</summary>
/// <param name="database">The class-owned migrated SQL catalog; each scenario uses independent rows.</param>
public sealed class MigrationSchemaTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    private const string Initial = "20260914130053_InitialSidequest";
    private static readonly Type[] BaselineTypes =
    [
        typeof(UserAccount), typeof(Administrator), typeof(Event), typeof(EventOwner),
        typeof(EventMembership), typeof(EventMembershipRequest), typeof(EventInvitation), typeof(Quest),
        typeof(QuestOwner), typeof(QuestInvitation), typeof(QuestParticipation), typeof(AuditEntry),
        typeof(EventStatusHistory), typeof(QuestStatusHistory), typeof(Notification), typeof(OutboxMessage)
    ];

    /// <summary>Checks the exact baseline migration identifier and required tables on the real SQL Server provider.</summary>
    /// <returns>A task completing after deployed-schema assertions.</returns>
    [Fact]
    public async Task MigrateAsync_EmptyOwnedDatabase_AppliesInitialSidequest()
    {
        await using var db = database.CreateContext();
        Assert.Equal([Initial], await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sys.tables").ToListAsync();
        foreach (var type in BaselineTypes)
            Assert.Contains(db.Model.FindEntityType(type)!.GetTableName()!, tables);
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName);
    }

    /// <summary>Checks that migrating an already-current catalog preserves aggregate content and rowversions.</summary>
    /// <returns>A task completing after a repeated migration and fresh-context persistence assertions.</returns>
    [Fact]
    public async Task MigrateAsync_AlreadyCurrent_PreservesSeededRows()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await using (var db = database.CreateContext())
            await db.Database.MigrateAsync();
        await using var read = database.CreateContext();
        Assert.Equal([Initial], await read.Database.GetAppliedMigrationsAsync());
        Assert.Equal("Sensitive Event sentinel", (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Name);
        Assert.Equal("Sensitive Quest sentinel", (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Title);
        Assert.Equal(seed.Event.Version, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Version);
    }

    /// <summary>Checks rollback and reapplication of the baseline migration in a separately owned disposable database.</summary>
    /// <returns>A task completing after old-row removal, fresh-seed verification, and owned-database cleanup.</returns>
    [Fact]
    public async Task Migration_DownToZero_ThenUp_RecreatesUsableSchema()
    {
        var owned = new SqlTestDatabase();
        try
        {
            await owned.InitializeAsync();
            var old = await FoundationSeed.CreateAsync(owned);
            await using (var db = owned.CreateContext())
            {
                await db.GetService<IMigrator>().MigrateAsync("0");
                Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
                var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sys.tables").ToListAsync();
                foreach (var type in BaselineTypes)
                    Assert.DoesNotContain(db.Model.FindEntityType(type)!.GetTableName()!, tables);
                await db.Database.MigrateAsync();
                Assert.Equal([Initial], await db.Database.GetAppliedMigrationsAsync());
            }
            var fresh = await FoundationSeed.CreateAsync(owned);
            await using var read = owned.CreateContext();
            Assert.False(await read.Users.AnyAsync(x => x.Id == old.User.Id));
            Assert.Equal(fresh.Other.Id, (await read.Events.SingleAsync()).CreatorId);
            Assert.Equal(2, await read.Users.CountAsync());
        }
        finally { await owned.DisposeAsync(); }
    }

    /// <summary>Checks that multiple owners persist equally and neither SQL nor EF introduces primary-owner or rank fields.</summary>
    /// <returns>A task completing after owner-row and model/catalog assertions.</returns>
    [Fact]
    public async Task Schema_EqualOwners_HasNoPrimaryOwnerRepresentation()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await FoundationSeed.PersistAsync(database,
            new EventOwner { EventId = seed.Event.Id, UserId = seed.User.Id },
            new EventOwner { EventId = seed.Event.Id, UserId = seed.Other.Id },
            new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id },
            new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.Other.Id });
        await using var read = database.CreateContext();
        Assert.Equal(2, await read.EventOwners.CountAsync(x => x.EventId == seed.Event.Id));
        Assert.Equal(2, await read.QuestOwners.CountAsync(x => x.QuestId == seed.Quest.Id));
        foreach (var type in new[] { typeof(Event), typeof(Quest), typeof(EventOwner), typeof(QuestOwner) })
        {
            var model = read.Model.FindEntityType(type)!;
            var columns = await ColumnsAsync(read, model.GetTableName()!);
            Assert.DoesNotContain(columns, IsPrimaryRepresentation);
            Assert.DoesNotContain(model.GetProperties(), x => IsPrimaryRepresentation(x.Name));
        }
    }

    /// <summary>Checks parent-zone conversion against a literal UTC instant and the absence of any independent Quest zone.</summary>
    /// <returns>A task completing after Prague conversion and deployed-model assertions.</returns>
    [Fact]
    public async Task Quest_UsesParentEventZone_WithoutIndependentZone()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await using var db = database.CreateContext();
        var quest = await db.Quests.SingleAsync(x => x.Id == seed.Quest.Id);
        var zone = (await db.Events.SingleAsync(x => x.Id == quest.EventId)).TimeZoneId;
        Assert.Equal("Europe/Prague", zone);
        var utc = TimeRules.ToUtc(new DateTime(2026, 7, 15, 12, 0, 0), zone);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), utc);
        Assert.Equal(TimeSpan.Zero, utc.Offset);
        var model = db.Model.FindEntityType(typeof(Quest))!;
        Assert.DoesNotContain(model.GetProperties(), x => x.Name.Contains("Zone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(await ColumnsAsync(db, model.GetTableName()!),
            x => x.Contains("Zone", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Checks pending-only Event relation indexes, unfiltered Quest grants, and non-null eight-byte SQL rowversions.</summary>
    /// <returns>A task completing after index-filter, column-type, and concurrency-token assertions.</returns>
    [Fact]
    public async Task Schema_PendingIndexesAndRowversion_MatchDeployedBehavior()
    {
        await using var db = database.CreateContext();
        foreach (var type in new[] { typeof(EventMembershipRequest), typeof(EventInvitation), typeof(QuestInvitation) })
        {
            var table = db.Model.FindEntityType(type)!.GetTableName()!;
            var filter = await db.Database.SqlQuery<string>($"""
                SELECT COALESCE(filter_definition, '') AS Value FROM sys.indexes
                WHERE object_id = OBJECT_ID({table}) AND is_unique = 1 AND is_primary_key = 0
                """).SingleAsync();
            Assert.Equal(type == typeof(QuestInvitation) ? "" : "[Status]=0",
                filter.Replace("(", "").Replace(")", "").Replace(" ", ""));
        }
        foreach (var type in new[] { typeof(Event), typeof(Quest), typeof(QuestParticipation), typeof(OutboxMessage) })
        {
            var table = db.Model.FindEntityType(type)!.GetTableName()!;
            var size = await db.Database.SqlQuery<int>($"""
                SELECT CAST(max_length AS int) AS Value FROM sys.columns
                WHERE object_id = OBJECT_ID({table}) AND name = 'Version' AND system_type_id = 189 AND is_nullable = 0
                """).SingleAsync();
            Assert.Equal(8, size);
            Assert.True(db.Model.FindEntityType(type)!.FindProperty("Version")!.IsConcurrencyToken);
        }
    }

    private static bool IsPrimaryRepresentation(string name) =>
        name.Equals("OwnerId", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Primary", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Rank", StringComparison.OrdinalIgnoreCase);

    private static Task<List<string>> ColumnsAsync(SidequestDbContext db, string table) =>
        db.Database.SqlQuery<string>($"SELECT name AS Value FROM sys.columns WHERE object_id = OBJECT_ID({table})").ToListAsync();

    /// <summary>Checks concurrent fixture isolation and cleanup of only two proven-owned catalogs while a third remains usable.</summary>
    /// <returns>A task completing after unique-name, row-isolation, catalog-removal, and repeat-disposal assertions.</returns>
    [Fact]
    public async Task SqlFixture_ParallelOwnedDatabases_AreDistinctAndOnlyOwnedCatalogsAreRemoved()
    {
        var first = new SqlTestDatabase();
        var second = new SqlTestDatabase();
        string firstName;
        string secondName;
        try
        {
            await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
            await using var firstContext = first.CreateContext();
            await using var secondContext = second.CreateContext();
            firstName = firstContext.Database.GetDbConnection().Database;
            secondName = secondContext.Database.GetDbConnection().Database;
            Assert.Matches("^SidequestTests_[0-9a-f]{32}$", firstName);
            Assert.Matches("^SidequestTests_[0-9a-f]{32}$", secondName);
            Assert.NotEqual(firstName, secondName);
            var row = FoundationSeed.NewUser();
            firstContext.Users.Add(row);
            await firstContext.SaveChangesAsync();
            Assert.False(await secondContext.Users.AnyAsync(x => x.Id == row.Id));
            Assert.Equal([Initial], await secondContext.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            await Task.WhenAll(first.DisposeAsync(), second.DisposeAsync());
        }
        // This third fixture must survive cleanup of the other two.
        await using var surviving = database.CreateContext();
        Assert.Equal([Initial], await surviving.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await surviving.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS Value FROM sys.databases WHERE name = {firstName} OR name = {secondName}
            """).SingleAsync());
        await first.DisposeAsync();
        await second.DisposeAsync();
    }
}
