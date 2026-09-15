using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Administration;

/// <summary>Loads business wording only after delivery authorization; captures exact rendered values before the first provider attempt.</summary>
public static class BusinessEmailComposer
{
    /// <summary>Uses current validated settings and latest template revision; an invalid stored override never silently falls back.</summary>
    /// <param name="db">Caller-owned authorized delivery transaction.</param>
    /// <param name="kind">Immutable source business intent; no protected content is supplied to templates.</param>
    /// <param name="cancellationToken">Cancels SQL reads.</param>
    /// <returns>Rendered snapshot to persist with delivery uncertainty and reuse across retries.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Stored settings/template or final rendering is invalid.</exception>
    public static async Task<RenderedBusinessEmail> ComposeAsync(ISidequestDbContext db, NotificationKind kind, CancellationToken cancellationToken)
    {
        var key = BusinessEmailRules.KeyFor(kind);
        var row = await db.NotificationTemplates.AsNoTracking().Where(x => x.Key == key).OrderByDescending(x => x.Revision)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var settings = await db.ApplicationSettings.AsNoTracking().Where(x => x.Key == BusinessEmailRules.BrandKey ||
            x.Key == BusinessEmailRules.ReplyToKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        return BusinessEmailRules.Render(row is null ? BusinessEmailRules.Default(key) : BusinessEmailService.ToTemplate(row), kind,
            settings.SingleOrDefault(x => x.Key == BusinessEmailRules.BrandKey)?.Value ?? "Sidequest",
            settings.SingleOrDefault(x => x.Key == BusinessEmailRules.ReplyToKey)?.Value);
    }
}
