using Microsoft.EntityFrameworkCore;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Verifies append-only template revisions and atomic mutation rejection through every persistence save overload.</summary>
/// <param name="database">Isolated migrated SQL fixture, never an application database.</param>
public sealed class NotificationTemplateHistoryTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Gets synchronous and asynchronous save selectors with default, true and false state-acceptance behavior.</summary>
    public static TheoryData<string> SaveOverloads => new()
    {
        "sync", "syncTrue", "syncFalse", "async", "asyncTrue", "asyncFalse"
    };

    /// <summary>Gets each save overload paired with forbidden modification and deletion.</summary>
    public static TheoryData<string, bool> MutationAttempts
    {
        get
        {
            var cases = new TheoryData<string, bool>();
            foreach (var overload in new[] { "sync", "syncTrue", "syncFalse", "async", "asyncTrue", "asyncFalse" })
                foreach (var delete in new[] { false, true })
                    cases.Add(overload, delete);
            return cases;
        }
    }

    /// <summary>Rejects changing or deleting an existing revision before an unrelated staged setting can be committed.</summary>
    /// <param name="overload">The save entry point exercised against the real context.</param>
    /// <param name="delete">Whether the attempted change removes the revision instead of overwriting its subject.</param>
    /// <returns>A task completing after exact Conflict and fresh persisted-state assertions.</returns>
    [Theory]
    [MemberData(nameof(MutationAttempts))]
    public async Task ExistingRevision_RejectsMutationAndNeighboringWriteAtomically(string overload, bool delete)
    {
        var original = await CreateRevisionAsync();
        var setting = new ApplicationSetting { Key = Guid.NewGuid().ToString("N"), Value = "Must not be committed." };
        Exception? error;
        await using (var db = database.CreateContext())
        {
            var tracked = await db.NotificationTemplates.SingleAsync(x => x.Id == original.Id);
            if (delete)
                db.NotificationTemplates.Remove(tracked);
            else
                tracked.Subject = "Forbidden replacement";
            db.ApplicationSettings.Add(setting);
            error = await Record.ExceptionAsync(() => SaveAsync(db, overload));
        }

        await using var read = database.CreateContext();
        var stored = await read.NotificationTemplates.SingleAsync(x => x.Id == original.Id);
        FoundationSeed.AssertConflict(error, "History records are immutable.");
        Assert.Equal(original.Key, stored.Key);
        Assert.Equal(1, stored.Revision);
        Assert.Equal("Original subject", stored.Subject);
        Assert.Equal("<p>Original HTML</p>", stored.HtmlBody);
        Assert.Equal("Original text", stored.TextBody);
        Assert.Equal(original.ChangedById, stored.ChangedById);
        Assert.Equal(FoundationSeed.Now, stored.ChangedUtc);
        Assert.Equal(original.Version, stored.Version);
        Assert.False(await read.ApplicationSettings.AnyAsync(x => x.Id == setting.Id));
    }

    /// <summary>Allows a new revision without changing the original version, content or identity.</summary>
    /// <param name="overload">The save entry point used to append the new revision.</param>
    /// <returns>A task completing after both revisions and save state-acceptance semantics are verified.</returns>
    [Theory]
    [MemberData(nameof(SaveOverloads))]
    public async Task NewRevision_AppendsWithoutChangingExistingHistory(string overload)
    {
        var original = await CreateRevisionAsync();
        var next = new NotificationTemplate
        {
            Key = original.Key,
            Revision = 2,
            Subject = "Revised subject",
            HtmlBody = "<p>Revised HTML</p>",
            TextBody = "Revised text",
            ChangedById = original.ChangedById,
            ChangedUtc = FoundationSeed.Now.AddMinutes(1)
        };
        await using (var db = database.CreateContext())
        {
            db.NotificationTemplates.Add(next);
            Assert.Equal(1, await SaveAsync(db, overload));
            Assert.Equal(overload.EndsWith("False", StringComparison.Ordinal) ? EntityState.Added : EntityState.Unchanged,
                db.Entry(next).State);
        }

        await using var read = database.CreateContext();
        var revisions = await read.NotificationTemplates.Where(x => x.Key == original.Key).OrderBy(x => x.Revision).ToArrayAsync();
        Assert.Collection(revisions,
            first =>
            {
                Assert.Equal(original.Id, first.Id);
                Assert.Equal(1, first.Revision);
                Assert.Equal("Original subject", first.Subject);
                Assert.Equal("<p>Original HTML</p>", first.HtmlBody);
                Assert.Equal("Original text", first.TextBody);
                Assert.Equal(FoundationSeed.Now, first.ChangedUtc);
                Assert.Equal(original.Version, first.Version);
            },
            second =>
            {
                Assert.Equal(next.Id, second.Id);
                Assert.Equal(2, second.Revision);
                Assert.Equal("Revised subject", second.Subject);
                Assert.Equal("<p>Revised HTML</p>", second.HtmlBody);
                Assert.Equal("Revised text", second.TextBody);
                Assert.Equal(original.ChangedById, second.ChangedById);
                Assert.Equal(FoundationSeed.Now.AddMinutes(1), second.ChangedUtc);
                Assert.Equal(8, second.Version.Length);
                Assert.NotEqual(original.Version, second.Version);
            });
    }

    private async Task<NotificationTemplate> CreateRevisionAsync()
    {
        var actor = FoundationSeed.NewUser();
        var template = new NotificationTemplate
        {
            Key = Guid.NewGuid().ToString("N"),
            Revision = 1,
            Subject = "Original subject",
            HtmlBody = "<p>Original HTML</p>",
            TextBody = "Original text",
            ChangedById = actor.Id,
            ChangedUtc = FoundationSeed.Now
        };
        await FoundationSeed.PersistAsync(database, actor, template);
        return template;
    }

    private static Task<int> SaveAsync(SidequestDbContext db, string overload) => overload switch
    {
        "sync" => Task.FromResult(db.SaveChanges()),
        "syncTrue" => Task.FromResult(db.SaveChanges(acceptAllChangesOnSuccess: true)),
        "syncFalse" => Task.FromResult(db.SaveChanges(acceptAllChangesOnSuccess: false)),
        "async" => db.SaveChangesAsync(),
        "asyncTrue" => db.SaveChangesAsync(acceptAllChangesOnSuccess: true, CancellationToken.None),
        "asyncFalse" => db.SaveChangesAsync(acceptAllChangesOnSuccess: false, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(overload))
    };
}
