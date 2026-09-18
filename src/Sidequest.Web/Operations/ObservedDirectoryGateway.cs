using Sidequest.Application.Abstractions;

namespace Sidequest.Web.Operations;

internal sealed class ObservedDirectoryGateway(IDirectoryGateway inner, OperationalActivityMetrics metrics) : IDirectoryGateway
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.DirectoryUserSearch, () => inner.SearchUsersAsync(query, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.DirectoryGroupSearch, () => inner.SearchGroupsAsync(query, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<DirectoryUser> GetUserAsync(Guid objectId, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.DirectoryUserLookup, () => inner.GetUserAsync(objectId, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryUser>> ExpandGroupAsync(Guid groupId, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.DirectoryGroupExpansion, () => inner.ExpandGroupAsync(groupId, cancellationToken), cancellationToken);
}
