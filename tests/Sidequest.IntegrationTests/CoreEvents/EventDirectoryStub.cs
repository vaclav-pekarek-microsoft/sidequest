using Sidequest.Application.Abstractions;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.CoreEvents;

internal sealed class EventDirectoryStub : IDirectoryGateway
{
    internal Dictionary<Guid, DirectoryUser> Users { get; } = [];
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
    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DirectoryUser>>(Users.Values.ToArray());
}
