using Microsoft.AspNetCore.Components.Authorization;
using Sidequest.Application.Abstractions;

namespace Sidequest.Web.Authentication;

public sealed class CircuitCurrentUser(
    AuthenticationStateProvider authenticationState, FoundationAuthenticationSettings settings, TimeProvider clock) : ICurrentUser
{
    public async ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await authenticationState.GetAuthenticationStateAsync();
        return WorkforceSession.IsCurrent(state.User, clock.GetUtcNow()) ? WorkforceIdentity.Read(state.User, settings) : null;
    }
}
