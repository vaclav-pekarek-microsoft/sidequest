using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Administration;

/// <summary>Authorizes nonsecret email configuration, synthetic previews and append-only template revision changes.</summary>
/// <param name="factory">Per-operation SQL contexts.</param>
/// <param name="access">Current persisted administrator authorization.</param>
/// <param name="clock">UTC audit clock.</param>
public sealed class BusinessEmailService(ISidequestDbContextFactory factory, IResourceAccess access, TimeProvider clock)
{
    /// <summary>Reads both allowlisted settings, returning compiled defaults when absent.</summary>
    /// <param name="cancellationToken">Cancels authorization and reads.</param>
    /// <param name="allowInvalidForEditing">Returns raw stored values only for encoded editor display and repair; callers must visibly validate them.</param>
    /// <returns>Brand and reply-to values with rowversion evidence; no infrastructure settings or secrets.</returns>
    /// <exception cref="DomainException">Authorization or stored setting validation fails.</exception>
    public async Task<IReadOnlyList<BusinessSetting>> GetSettingsAsync(CancellationToken cancellationToken = default, bool allowInvalidForEditing = false)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var rows = await db.ApplicationSettings.AsNoTracking()
            .Where(x => x.Key == BusinessEmailRules.BrandKey || x.Key == BusinessEmailRules.ReplyToKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new[] { BusinessEmailRules.BrandKey, BusinessEmailRules.ReplyToKey }.Select(key =>
        {
            var row = rows.SingleOrDefault(x => x.Key == key);
            var value = row?.Value ?? (key == BusinessEmailRules.BrandKey ? "Sidequest" : "");
            return new BusinessSetting(key, allowInvalidForEditing ? value : BusinessEmailRules.ValidateSetting(key, value), row?.Version ?? []);
        }).ToArray();
    }

    /// <summary>Writes one allowlisted setting with rowversion/create-only concurrency and a value-free audit record.</summary>
    /// <param name="setting">Key, new value and original rowversion; empty version expects no database row.</param>
    /// <param name="cancellationToken">Cancels transactional reads and writes.</param>
    /// <returns>A task completing after setting and audit commit together.</returns>
    /// <exception cref="DomainException">Forbidden actor, invalid input or stale rowversion; edits are not silently overwritten.</exception>
    public async Task SaveSettingAsync(BusinessSetting setting, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var value = BusinessEmailRules.ValidateSetting(setting.Key, setting.Value);
        var row = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Key == setting.Key, cancellationToken).ConfigureAwait(false);
        if (setting.Version is null || !(row?.Version ?? []).SequenceEqual(setting.Version))
            throw Conflict();
        if (row is null)
        {
            row = new ApplicationSetting { Key = setting.Key };
            db.ApplicationSettings.Add(row);
        }
        row.Value = value;
        Audit(db, row.Id, actor.Id, "BusinessEmailSettingChanged", setting.Key);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a page of retained revisions, newest first, followed by the compiled default.</summary>
    /// <param name="key">Allowlisted template category.</param>
    /// <param name="cancellationToken">Cancels authorization and reads.</param>
    /// <param name="page">Optional one-based history page, default 25 and maximum 100 revisions.</param>
    /// <param name="allowInvalidForEditing">Returns raw invalid wording only for encoded editor/history display and repair.
    /// It does not authorize preview/render/send success; callers must visibly validate the selected draft.</param>
    /// <returns>Immutable revision DTOs; corrupt stored overrides fail explicitly unless raw editing was requested.</returns>
    /// <exception cref="DomainException">Authorization, unknown key or stored template validation fails.</exception>
    public async Task<IReadOnlyList<EmailTemplate>> GetHistoryAsync(string key, CancellationToken cancellationToken = default,
        PageRequest? page = null, bool allowInvalidForEditing = false)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var compiled = BusinessEmailRules.Default(key);
        page ??= new();
        var rows = await db.NotificationTemplates.AsNoTracking().Where(x => x.Key == key)
            .OrderByDescending(x => x.Revision).Skip(page.Offset).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = rows.Select(ToTemplate).ToList();
        if (!allowInvalidForEditing)
            result.ForEach(BusinessEmailRules.ValidateTemplate);
        result.Add(compiled);
        return result;
    }

    /// <summary>Appends a revision without ever modifying/deleting a previous template; restore defaults by saving their wording as a new revision.</summary>
    /// <param name="template">New wording; Revision is the last observed revision, zero for the compiled default.</param>
    /// <param name="cancellationToken">Cancels authorization, concurrency reads and commit.</param>
    /// <returns>The newly allocated revision number.</returns>
    /// <exception cref="DomainException">Actor is forbidden, validation fails or another revision was appended first.</exception>
    public async Task<int> SaveTemplateAsync(EmailTemplate template, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        BusinessEmailRules.ValidateTemplate(template);
        var latest = await db.NotificationTemplates.Where(x => x.Key == template.Key).MaxAsync(x => (int?)x.Revision, cancellationToken).ConfigureAwait(false) ?? 0;
        if (latest != template.Revision)
            throw Conflict();
        var row = new NotificationTemplate
        {
            Key = template.Key,
            Revision = checked(latest + 1),
            Subject = template.Subject,
            HtmlBody = template.HtmlBody,
            TextBody = template.TextBody,
            ChangedById = actor.Id,
            ChangedUtc = clock.GetUtcNow()
        };
        db.NotificationTemplates.Add(row);
        Audit(db, row.Id, actor.Id, "EmailTemplateRevisionAdded", $"{row.Key}:{row.Revision}");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row.Revision;
    }

    /// <summary>Renders an unsaved template with fixed synthetic values only; it never accepts a resource ID or fetches private content.</summary>
    /// <param name="template">Unsaved wording to validate.</param>
    /// <param name="cancellationToken">Cancels administrator authorization.</param>
    /// <returns>Safe synthetic preview, not a sent email or provider-acceptance claim.</returns>
    /// <exception cref="DomainException">Authorization, key or template validation fails.</exception>
    public async Task<RenderedBusinessEmail> PreviewAsync(EmailTemplate template, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var kind = Enum.GetValues<NotificationKind>().SingleOrDefault(x => BusinessEmailRules.KeyFor(x) == template.Key);
        return BusinessEmailRules.Render(template, kind, "Synthetic Sidequest", "reply@example.invalid");
    }

    internal static EmailTemplate ToTemplate(NotificationTemplate row) =>
        new(row.Key, row.Revision, row.Subject, row.HtmlBody, row.TextBody, row.ChangedUtc);

    private void Audit(ISidequestDbContext db, Guid id, Guid actorId, string action, string key) =>
        db.AuditEntries.Add(new AuditEntry
        {
            ResourceKind = ResourceKind.System,
            ResourceId = id,
            ActorId = actorId,
            Action = action,
            Reason = key,
            CorrelationId = Guid.NewGuid().ToString("N"),
            OccurredUtc = clock.GetUtcNow()
        });

    private static DomainException Conflict() => new(ErrorCode.Conflict, "Configuration changed. Reload and compare before saving; your unsaved wording is preserved.");
}
