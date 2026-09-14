using Sidequest.Application.Abstractions;

namespace Sidequest.Application.Events.Implementation;

internal sealed class SavedActorCurrentUser(UserIdentity identity) : ICurrentUser
{
    /// <inheritdoc />
    public ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<UserIdentity?>(identity);
    }
}
