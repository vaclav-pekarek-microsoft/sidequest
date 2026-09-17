using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Web.Operations;

internal sealed class ObservedResourceAccess(IResourceAccess inner, OperationalActivityMetrics metrics) : IResourceAccess
{
    /// <inheritdoc />
    public Task<UserAccount> RequireUserAsync(ISidequestDbContext db, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.UserAuthorization, () => inner.RequireUserAsync(db, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<UserAccount> RequireAdministratorAsync(ISidequestDbContext db, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.AdministratorAuthorization,
            () => inner.RequireAdministratorAsync(db, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<Event> RequireEventAsync(ISidequestDbContext db, Guid eventId, Guid userId,
        bool ownerOnly = false, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.EventAuthorization,
            () => inner.RequireEventAsync(db, eventId, userId, ownerOnly, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<Quest> RequireQuestAsync(ISidequestDbContext db, Guid questId, Guid userId,
        bool ownerOnly = false, bool moderation = false, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.QuestAuthorization,
            () => inner.RequireQuestAsync(db, questId, userId, ownerOnly, moderation, cancellationToken), cancellationToken);
}
