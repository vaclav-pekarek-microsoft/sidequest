using Sidequest.Application.Abstractions;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.CoreEvents;

internal sealed class EventDirectoryStub : IDirectoryGateway
{
    internal Dictionary<Guid, DirectoryUser> Users { get; } = [];
    internal Func<CancellationToken, Task<IReadOnlyList<DirectoryUser>>>? Expand { get; set; }
    internal int ExpansionCalls { get; private set; }
    internal int UserCalls { get; private set; }

    /// <inheritdoc />
    public Task<DirectoryUser> GetUserAsync(Guid objectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UserCalls++;
        return Task.FromResult(Users.TryGetValue(objectId, out var user)
            ? user : throw new DomainException(ErrorCode.Validation, "Synthetic identity unavailable."));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryUser>> ExpandGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExpansionCalls++;
        return Expand is null ? Task.FromResult<IReadOnlyList<DirectoryUser>>(Users.Values.ToArray()) : Expand(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DirectoryUser>>(Users.Values.ToArray());

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("This scenario did not configure group search.");
}
