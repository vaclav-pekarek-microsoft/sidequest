using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Administration;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.SecondaryAdministration;

/// <summary>Real SQL setting rowversions, immutable revision service behavior and explicit stored-override validation.</summary>
public sealed class BusinessEmailServiceTests
{
    /// <summary>Defaults create no rows; setting saves audit atomically and stale create/update versions cannot overwrite another value.</summary>
    [Fact]
    public async Task SettingsUseAllowlistRowversionsAndValueFreeAudit()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var defaults = await s.Email.GetSettingsAsync();
        var brand = defaults.Single(x => x.Key == BusinessEmailRules.BrandKey);
        Assert.Equal("Sidequest", brand.Value);
        Assert.Empty(brand.Version);
        await s.Email.SaveSettingAsync(brand with { Value = "First brand" });
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Email.SaveSettingAsync(brand with { Value = "Lost update" }))).Code);
        var latest = (await s.Email.GetSettingsAsync()).Single(x => x.Key == brand.Key);
        await s.Email.SaveSettingAsync(latest with { Value = "Second brand" });
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Email.SaveSettingAsync(latest with { Value = "Stale update" }))).Code);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Email.SaveSettingAsync(new("email.sender", "unverified@example.invalid", [])))).Code);
        await using var db = s.Database.CreateContext();
        Assert.Equal("Second brand", (await db.ApplicationSettings.SingleAsync()).Value);
        var audits = await db.AuditEntries.ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.All(audits, x =>
        {
            Assert.Equal("BusinessEmailSettingChanged", x.Action);
            Assert.Equal(BusinessEmailRules.BrandKey, x.Reason);
            Assert.Equal(s.Seed.User.Id, x.ActorId);
        });
    }

    /// <summary>Revision saves retain exact previous wording, reject stale append attempts and create synthetic previews without private data.</summary>
    [Fact]
    public async Task TemplateHistoryAppendsAndConflictsWithoutOverwriting()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var compiled = Assert.Single(await s.Email.GetHistoryAsync("quest.invitation"));
        Assert.Equal(0, compiled.Revision);
        var first = compiled with { Subject = "{{Brand}} invitation one" };
        Assert.Equal(1, await s.Email.SaveTemplateAsync(first));
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => s.Email.SaveTemplateAsync(first))).Code);
        var second = first with { Revision = 1, Subject = "{{Brand}} invitation two" };
        Assert.Equal(2, await s.Email.SaveTemplateAsync(second));
        var history = await s.Email.GetHistoryAsync(first.Key);
        Assert.Equal(new[] { 2, 1, 0 }, history.Select(x => x.Revision));
        Assert.Equal("{{Brand}} invitation one", history[1].Subject);
        Assert.Equal("{{Brand}} invitation two", history[0].Subject);
        var older = await s.Email.GetHistoryAsync(first.Key, page: new(2, 1));
        Assert.Equal(new[] { 1, 0 }, older.Select(x => x.Revision));
        Assert.Equal(first.Subject, older[0].Subject);
        var preview = await s.Email.PreviewAsync(second);
        Assert.Equal("Synthetic Sidequest invitation two", preview.Subject);
        Assert.Equal("reply@example.invalid", preview.ReplyTo);
        Assert.DoesNotContain(s.Seed.Quest.Title, preview.HtmlBody, StringComparison.Ordinal);
        await using var db = s.Database.CreateContext();
        Assert.Equal(2, await db.NotificationTemplates.CountAsync());
        Assert.Equal(2, await db.AuditEntries.CountAsync(x => x.Action == "EmailTemplateRevisionAdded"));
        Assert.Empty(await db.NotificationDeliveries.ToArrayAsync());
    }

    /// <summary>Corrupted stored overrides and invalid saves fail explicitly rather than masquerading as valid compiled defaults.</summary>
    [Fact]
    public async Task InvalidStoredOverrideNeverFallsBackAndInvalidSaveIsAtomic()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var invalid = BusinessEmailRules.Default("access.removed") with { Subject = "{{Quest.Title}}" };
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() => s.Email.SaveTemplateAsync(invalid))).Code);
        await using (var db = s.Database.CreateContext())
        {
            Assert.Empty(await db.AuditEntries.ToArrayAsync());
            Assert.Empty(await db.NotificationTemplates.ToArrayAsync());
            db.NotificationTemplates.Add(new NotificationTemplate
            {
                Key = invalid.Key,
                Revision = 1,
                Subject = invalid.Subject,
                HtmlBody = invalid.HtmlBody,
                TextBody = invalid.TextBody,
                ChangedUtc = s.Clock.Now,
                ChangedById = s.Seed.User.Id
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() => s.Email.GetHistoryAsync(invalid.Key))).Code);
        await using var read = s.Database.CreateContext();
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            BusinessEmailComposer.ComposeAsync(read, NotificationKind.AccessRemoved, CancellationToken.None))).Code);
        var editable = await s.Email.GetHistoryAsync(invalid.Key, allowInvalidForEditing: true);
        Assert.Equal("{{Quest.Title}}", editable[0].Subject);
        Assert.Equal(2, await s.Email.SaveTemplateAsync(BusinessEmailRules.Default(invalid.Key) with { Revision = editable[0].Revision }));
        await using var repairedContext = s.Database.CreateContext();
        var repaired = await BusinessEmailComposer.ComposeAsync(repairedContext, NotificationKind.AccessRemoved, CancellationToken.None);
        Assert.Equal(2, repaired.Revision);
        Assert.Equal("Sidequest notification", repaired.Subject);
    }

    /// <summary>Competing first revisions cannot both claim revision one or lose the winning audit.</summary>
    [Fact]
    public async Task CompetingTemplateAppendsRetainOneWinnerAndCompleteHistory()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var first = BusinessEmailRules.Default("quest.joined") with { Subject = "First {{Brand}}" };
        var second = first with { Subject = "Second {{Brand}}" };
        var results = await Task.WhenAll(Record.ExceptionAsync(() => s.Email.SaveTemplateAsync(first)),
            Record.ExceptionAsync(() => s.Email.SaveTemplateAsync(second)));
        Assert.Single(results, x => x is null);
        Assert.Equal(ErrorCode.Conflict, Assert.IsType<DomainException>(Assert.Single(results, x => x is not null)).Code);
        await using var db = s.Database.CreateContext();
        var saved = Assert.Single(await db.NotificationTemplates.ToArrayAsync());
        Assert.Equal(1, saved.Revision);
        Assert.Contains(saved.Subject, new[] { first.Subject, second.Subject });
        Assert.Single(await db.AuditEntries.ToArrayAsync());
    }

    /// <summary>Invalid stored settings fail composition, but an authorized encoded editor can repair the existing row with its actual rowversion.</summary>
    [Fact]
    public async Task InvalidStoredReplyToRequiresValidatedRowversionRepair()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await using (var db = s.Database.CreateContext())
        {
            db.ApplicationSettings.Add(new ApplicationSetting { Key = BusinessEmailRules.ReplyToKey, Value = "invalid mailbox" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() => s.Email.GetSettingsAsync())).Code);
        await using (var db = s.Database.CreateContext())
        {
            Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
                BusinessEmailComposer.ComposeAsync(db, NotificationKind.Joined, CancellationToken.None))).Code);
        }
        var editor = (await s.Email.GetSettingsAsync(allowInvalidForEditing: true)).Single(x => x.Key == BusinessEmailRules.ReplyToKey);
        Assert.Equal("invalid mailbox", editor.Value);
        await s.Email.SaveSettingAsync(editor with { Value = "fixed@example.invalid" });
        await using var repaired = s.Database.CreateContext();
        var message = await BusinessEmailComposer.ComposeAsync(repaired, NotificationKind.Joined, CancellationToken.None);
        Assert.Equal("fixed@example.invalid", message.ReplyTo);
        Assert.Equal("Sidequest notification", message.Subject);
        Assert.Single(await repaired.AuditEntries.ToArrayAsync());
    }
}
