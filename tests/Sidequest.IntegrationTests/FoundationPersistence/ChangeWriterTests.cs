using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks exact durable change envelopes and atomic aggregate, audit, status-history, and outbox persistence.</summary>
/// <param name="database">The isolated migrated SQL fixture used for independently scoped outbox scenarios.</param>
public sealed class ChangeWriterTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    private const string Change = "10000000-0000-0000-0000-000000000001";
    private const string Parent = "20000000-0000-0000-0000-000000000002";
    private const string Child = "30000000-0000-0000-0000-000000000003";
    private const string Actor = "40000000-0000-0000-0000-000000000004";
    private const string RecipientA = "50000000-0000-0000-0000-000000000005";
    private const string RecipientB = "60000000-0000-0000-0000-000000000006";
    private const string Reason = "Changed \"room\"\nPříliš žluťoučký";
    private static readonly string[] Properties =
    [
        "ChangeId", "Kind", "EventId", "QuestId", "ActorId", "RecipientIds",
        "OccurredUtc", "CalendarRevision", "Reason", "PreviousAttendeeIds", "CalendarChanged", "MaterialChange", "AffectedUserIds"
    ];

    /// <summary>Checks the full literal serialized envelope, work metadata, and staging-only behavior before an explicit save.</summary>
    /// <returns>A task completing after pre-save SQL absence and post-save payload/rowversion assertions.</returns>
    [Fact]
    public async Task Append_StagesOneExactOutboxMessage_WithoutSaving()
    {
        var envelope = new ChangeEnvelope(Guid.Parse(Change), NotificationKind.QuestUpdated, Guid.Parse(Parent),
            Guid.Parse(Child), Guid.Parse(Actor), [Guid.Parse(RecipientB), Guid.Parse(RecipientA)],
            FoundationSeed.Now, 4294967301L, Reason, [Guid.Parse(RecipientA), Guid.Parse(RecipientB)],
            AffectedUserIds: [Guid.Parse(RecipientA)]);
        await using (var db = database.CreateContext())
        {
            new ChangeWriter().Append(db, envelope);
            var entry = Assert.Single(db.ChangeTracker.Entries());
            Assert.Equal(EntityState.Added, entry.State);
            var row = Assert.IsType<OutboxMessage>(entry.Entity);
            Assert.Empty(row.Version);
            AssertMetadata(row, Guid.Parse(Change), Guid.Parse(Child));
            AssertFullPayload(row.PayloadJson);
            await using (var before = database.CreateContext())
                Assert.False(await before.OutboxMessages.AnyAsync(x => x.Id == Guid.Parse(Change)));
            Assert.Equal(1, await db.SaveChangesAsync());
        }
        await using var read = database.CreateContext();
        var stored = await read.OutboxMessages.SingleAsync(x => x.Id == Guid.Parse(Change));
        AssertMetadata(stored, Guid.Parse(Change), Guid.Parse(Child));
        Assert.Equal("10000000000000000000000000000001", stored.CorrelationId);
        Assert.Equal(8, stored.Version.Length);
        AssertFullPayload(stored.PayloadJson);
    }

    /// <summary>Checks Event aggregate selection and explicit serialization of null, empty, and default envelope fields.</summary>
    /// <returns>A task completing after durable JSON and pending-work metadata assertions.</returns>
    [Fact]
    public async Task Append_EventAggregateAndDefaults_RetainsNullAndEmptyFields()
    {
        var id = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            new ChangeWriter().Append(db, new(id, NotificationKind.QuestPublished, Guid.Parse(Parent),
                null, null, [], FoundationSeed.Now));
            Assert.Equal(1, await db.SaveChangesAsync());
        }
        await using var read = database.CreateContext();
        var row = await read.OutboxMessages.SingleAsync(x => x.Id == id);
        AssertMetadata(row, id, Guid.Parse(Parent));
        Assert.Equal(8, row.Version.Length);
        using var json = JsonDocument.Parse(row.PayloadJson);
        var root = json.RootElement;
        AssertPropertyNames(root);
        Assert.Equal(id.ToString("D"), root.GetProperty("ChangeId").GetString());
        Assert.Equal(Parent, root.GetProperty("EventId").GetString());
        Assert.Equal(0, root.GetProperty("Kind").GetInt32());
        Assert.Equal("2026-07-15T10:00:00+00:00", root.GetProperty("OccurredUtc").GetString());
        foreach (var property in new[] { "QuestId", "ActorId", "PreviousAttendeeIds", "AffectedUserIds" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty(property).ValueKind);
        Assert.Equal(0L, root.GetProperty("CalendarRevision").GetInt64());
        Assert.Equal("", root.GetProperty("Reason").GetString());
        Assert.False(root.GetProperty("CalendarChanged").GetBoolean());
        Assert.False(root.GetProperty("MaterialChange").GetBoolean());
        Assert.Empty(root.GetProperty("RecipientIds").EnumerateArray());
    }

    /// <summary>Checks that recipient and prior-attendee arrays preserve order, duplicates, and null-versus-empty meaning.</summary>
    /// <param name="partition">The empty, singleton, or multiple/repeated-recipient array partition.</param>
    /// <param name="nullPrevious">Whether prior attendees are absent rather than an explicit array.</param>
    /// <returns>A task completing after exact serialized-array and stored-work assertions.</returns>
    [Theory]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("singleton", false)]
    [InlineData("multiple", false)]
    public async Task Append_ArrayPartitions_PreserveSuppliedOrderAndMultiplicity(string partition, bool nullPrevious)
    {
        string[] expected = partition switch
        {
            "empty" => [],
            "singleton" => [RecipientB],
            "multiple" => [RecipientB, RecipientA, RecipientB],
            _ => throw new ArgumentOutOfRangeException(nameof(partition))
        };
        var id = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            new ChangeWriter().Append(db, new(id, NotificationKind.QuestUpdated, Guid.Parse(Parent),
                Guid.Parse(Child), null, expected.Select(Guid.Parse).ToArray(), FoundationSeed.Now,
                PreviousAttendeeIds: nullPrevious ? null : expected.Select(Guid.Parse).ToArray()));
            Assert.Equal(1, await db.SaveChangesAsync());
        }
        await using var read = database.CreateContext();
        var row = await read.OutboxMessages.SingleAsync(x => x.Id == id);
        using var json = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal(expected, json.RootElement.GetProperty("RecipientIds").EnumerateArray().Select(x => x.GetString()));
        var previous = json.RootElement.GetProperty("PreviousAttendeeIds");
        if (nullPrevious)
            Assert.Equal(JsonValueKind.Null, previous.ValueKind);
        else
            Assert.Equal(expected, previous.EnumerateArray().Select(x => x.GetString()));
        AssertMetadata(row, id, Guid.Parse(Child));
        Assert.Equal(8, row.Version.Length);
    }

    /// <summary>Checks distinct change identities/correlations and literal payload values without an implicit writer save.</summary>
    /// <returns>A task completing after staged counts and fresh-context inspection of both outbox messages.</returns>
    [Fact]
    public async Task Append_TwoDistinctChanges_StagesAndPersistsExactlyTwoMessages()
    {
        var aggregate = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            var writer = new ChangeWriter();
            writer.Append(db, new(first, NotificationKind.Joined, aggregate, null, null, [], FoundationSeed.Now, Reason: "First"));
            writer.Append(db, new(second, NotificationKind.Left, aggregate, null, null, [], FoundationSeed.Now, Reason: "Second"));
            Assert.Equal(2, db.ChangeTracker.Entries<OutboxMessage>().Count(x => x.State == EntityState.Added));
            await using (var before = database.CreateContext())
                Assert.False(await before.OutboxMessages.AnyAsync(x => x.AggregateId == aggregate));
            Assert.Equal(2, await db.SaveChangesAsync());
        }
        await using var read = database.CreateContext();
        var rows = await read.OutboxMessages.Where(x => x.AggregateId == aggregate).ToListAsync();
        Assert.Equal(2, rows.Count);
        AssertMessage(first, "First", 7);
        AssertMessage(second, "Second", 8);
        void AssertMessage(Guid id, string reason, int kind)
        {
            var row = Assert.Single(rows, x => x.Id == id);
            AssertMetadata(row, id, aggregate);
            using var json = JsonDocument.Parse(row.PayloadJson);
            Assert.Equal(reason, json.RootElement.GetProperty("Reason").GetString());
            Assert.Equal(kind, json.RootElement.GetProperty("Kind").GetInt32());
        }
    }

    /// <summary>Checks that reusing a persisted change ID reports Conflict and preserves the original payload.</summary>
    /// <returns>A task completing after the rejected save and fresh original-message assertions.</returns>
    [Fact]
    public async Task Append_DuplicatePersistedChangeId_ConflictsWithoutReplacingMessage()
    {
        var id = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var envelope = new ChangeEnvelope(id, NotificationKind.Joined, parent, null, null, [], FoundationSeed.Now, Reason: "Original");
        await using (var db = database.CreateContext())
        {
            new ChangeWriter().Append(db, envelope);
            await db.SaveChangesAsync();
        }
        await using (var failure = database.CreateContext())
        {
            new ChangeWriter().Append(failure, envelope with { Reason = "Replacement", Kind = NotificationKind.Left });
            FoundationSeed.AssertConflict(await Record.ExceptionAsync(() => failure.SaveChangesAsync()), FoundationSeed.DuplicateMessage);
        }
        await using var read = database.CreateContext();
        var row = await read.OutboxMessages.SingleAsync(x => x.AggregateId == parent);
        AssertMetadata(row, id, parent);
        using var json = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal("Original", json.RootElement.GetProperty("Reason").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("Kind").GetInt32());
    }

    /// <summary>Checks that one transaction durably commits the aggregate, audit entry, status history, and outbox together.</summary>
    /// <param name="quest">True for a Quest aggregate; false for an Event aggregate.</param>
    /// <returns>A task completing after transaction disposal and exact fresh-context row/content assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AggregateAndOutbox_Commit_SurviveContextDisposal(bool quest) => TransactionAsync(quest, true);

    /// <summary>Checks that rollback removes all four inserted change rows while preserving their prerequisite seed data.</summary>
    /// <param name="quest">True for a Quest aggregate; false for an Event aggregate.</param>
    /// <returns>A task completing after in-transaction presence and post-rollback absence assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AggregateAndOutbox_Rollback_PersistsNeither(bool quest) => TransactionAsync(quest, false);

    private async Task TransactionAsync(bool quest, bool commit)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        Entity aggregate = quest ? FoundationSeed.NewQuest(seed.Event.Id, seed.User.Id) : FoundationSeed.NewEvent(seed.User.Id);
        var change = Guid.NewGuid();
        var audit = new AuditEntry
        {
            ResourceKind = quest ? ResourceKind.Quest : ResourceKind.Event,
            ResourceId = aggregate.Id,
            ActorId = seed.User.Id,
            Action = "Published",
            Reason = "Foundation transaction",
            CorrelationId = change.ToString("N"),
            OccurredUtc = FoundationSeed.Now
        };
        Entity history = quest
            ? new QuestStatusHistory
            {
                QuestId = aggregate.Id,
                Previous = QuestStatus.Draft,
                Next = QuestStatus.Active,
                ActorId = seed.User.Id,
                Reason = "Foundation transaction",
                OccurredUtc = FoundationSeed.Now
            }
            : new EventStatusHistory
            {
                EventId = aggregate.Id,
                Previous = EventStatus.Draft,
                Next = EventStatus.Active,
                ActorId = seed.User.Id,
                Reason = "Foundation transaction",
                OccurredUtc = FoundationSeed.Now
            };
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);
            db.Add(aggregate);
            db.AddRange(audit, history);
            new ChangeWriter().Append(db, new(change, NotificationKind.QuestPublished,
                quest ? seed.Event.Id : aggregate.Id, quest ? aggregate.Id : null, seed.User.Id, [seed.Other.Id], FoundationSeed.Now));
            Assert.Equal(4, await db.SaveChangesAsync());
            // SQL queries prove all four INSERTs happened before the rollback.
            Assert.True(quest
                ? await db.Quests.AsNoTracking().AnyAsync(x => x.Id == aggregate.Id)
                : await db.Events.AsNoTracking().AnyAsync(x => x.Id == aggregate.Id));
            Assert.True(await db.OutboxMessages.AsNoTracking().AnyAsync(x => x.Id == change));
            Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(x => x.Id == audit.Id));
            Assert.True(quest
                ? await db.QuestStatusHistory.AsNoTracking().AnyAsync(x => x.Id == history.Id)
                : await db.EventStatusHistory.AsNoTracking().AnyAsync(x => x.Id == history.Id));
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        Assert.Equal(commit, quest ? await read.Quests.AnyAsync(x => x.Id == aggregate.Id) : await read.Events.AnyAsync(x => x.Id == aggregate.Id));
        var message = await read.OutboxMessages.SingleOrDefaultAsync(x => x.Id == change);
        var storedAudit = await read.AuditEntries.SingleOrDefaultAsync(x => x.Id == audit.Id);
        var storedHistory = (Entity?)await read.FindAsync(history.GetType(), history.Id);
        if (commit)
        {
            Assert.NotNull(message);
            AssertMetadata(message, change, aggregate.Id);
            Assert.Equal(8, message.Version.Length);
            Assert.NotNull(storedAudit);
            Assert.Equal("Published", storedAudit.Action);
            Assert.Equal("Foundation transaction", storedAudit.Reason);
            Assert.Equal(aggregate.Id, storedAudit.ResourceId);
            Assert.Equal(seed.User.Id, storedAudit.ActorId);
            Assert.Equal(change.ToString("N"), storedAudit.CorrelationId);
            Assert.Equal(FoundationSeed.Now, storedAudit.OccurredUtc);
            Assert.Equal(8, storedAudit.Version.Length);
            Assert.NotNull(storedHistory);
            Assert.Equal("Foundation transaction", read.Entry(storedHistory).Property("Reason").CurrentValue);
            Assert.Equal(seed.User.Id, read.Entry(storedHistory).Property("ActorId").CurrentValue);
            Assert.Equal(FoundationSeed.Now, read.Entry(storedHistory).Property("OccurredUtc").CurrentValue);
            Assert.Equal(8, storedHistory.Version.Length);
            if (quest)
            {
                var status = Assert.IsType<QuestStatusHistory>(storedHistory);
                Assert.Equal(QuestStatus.Draft, status.Previous);
                Assert.Equal(QuestStatus.Active, status.Next);
                Assert.Equal(aggregate.Id, status.QuestId);
            }
            else
            {
                var status = Assert.IsType<EventStatusHistory>(storedHistory);
                Assert.Equal(EventStatus.Draft, status.Previous);
                Assert.Equal(EventStatus.Active, status.Next);
                Assert.Equal(aggregate.Id, status.EventId);
            }
            if (quest)
                Assert.Equal("Sensitive Quest sentinel", (await read.Quests.SingleAsync(x => x.Id == aggregate.Id)).Title);
            else
                Assert.Equal("Sensitive Event sentinel", (await read.Events.SingleAsync(x => x.Id == aggregate.Id)).Name);
        }
        else
        {
            Assert.Null(message);
            Assert.Null(storedAudit);
            Assert.Null(storedHistory);
        }
        Assert.Equal("Synthetic Ada", (await read.Users.SingleAsync(x => x.Id == seed.User.Id)).DisplayName);
        Assert.True(await read.Events.AnyAsync(x => x.Id == seed.Event.Id));
    }

    private static void AssertMetadata(OutboxMessage row, Guid id, Guid aggregate)
    {
        Assert.Equal(id, row.Id);
        Assert.Equal("sidequest.change.v1", row.Type);
        Assert.Equal(1, row.SchemaVersion);
        Assert.Equal(aggregate, row.AggregateId);
        Assert.Equal(id.ToString("N"), row.CorrelationId);
        Assert.Equal(FoundationSeed.Now, row.OccurredUtc);
        Assert.Equal(FoundationSeed.Now, row.DueUtc);
        Assert.Equal(TimeSpan.Zero, row.OccurredUtc.Offset);
        Assert.Equal(TimeSpan.Zero, row.DueUtc.Offset);
        Assert.Equal(WorkStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LeaseId);
        Assert.Null(row.LeaseUntilUtc);
        Assert.Null(row.LastError);
    }

    private static void AssertFullPayload(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        AssertPropertyNames(root);
        Assert.Equal(Change, root.GetProperty("ChangeId").GetString());
        Assert.Equal(Parent, root.GetProperty("EventId").GetString());
        Assert.Equal(Child, root.GetProperty("QuestId").GetString());
        Assert.Equal(Actor, root.GetProperty("ActorId").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("Kind").ValueKind);
        Assert.Equal(10, root.GetProperty("Kind").GetInt32());
        Assert.Equal("2026-07-15T10:00:00+00:00", root.GetProperty("OccurredUtc").GetString());
        Assert.Equal(4294967301L, root.GetProperty("CalendarRevision").GetInt64());
        Assert.Equal(Reason, root.GetProperty("Reason").GetString());
        Assert.False(root.GetProperty("CalendarChanged").GetBoolean());
        Assert.False(root.GetProperty("MaterialChange").GetBoolean());
        Assert.Equal([RecipientB, RecipientA], root.GetProperty("RecipientIds").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal([RecipientA, RecipientB], root.GetProperty("PreviousAttendeeIds").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal([RecipientA], root.GetProperty("AffectedUserIds").EnumerateArray().Select(x => x.GetString()));
    }

    private static void AssertPropertyNames(JsonElement root) =>
        Assert.Equal(Properties.Order(), root.EnumerateObject().Select(x => x.Name).Order());

    /// <summary>Checks independent calendar/material change flags rather than conflating either delivery decision.</summary>
    /// <param name="calendarChanged">Whether this envelope requests calendar-relevant processing.</param>
    /// <param name="materialChange">Whether this envelope marks a material attendee change.</param>
    /// <returns>A task completing after exact serialized booleans and durable work metadata assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Append_ChangeFlags_SerializeIndependently(bool calendarChanged, bool materialChange)
    {
        var id = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            new ChangeWriter().Append(db, new(id, NotificationKind.QuestUpdated, Guid.Parse(Parent),
                Guid.Parse(Child), null, [], FoundationSeed.Now,
                CalendarChanged: calendarChanged, MaterialChange: materialChange));
            Assert.Equal(1, await db.SaveChangesAsync());
        }
        await using var read = database.CreateContext();
        var row = await read.OutboxMessages.SingleAsync(x => x.Id == id);
        using var json = JsonDocument.Parse(row.PayloadJson);
        AssertPropertyNames(json.RootElement);
        Assert.Equal(calendarChanged, json.RootElement.GetProperty("CalendarChanged").GetBoolean());
        Assert.Equal(materialChange, json.RootElement.GetProperty("MaterialChange").GetBoolean());
        Assert.Equal(10, json.RootElement.GetProperty("Kind").GetInt32());
        AssertMetadata(row, id, Guid.Parse(Child));
    }
}
