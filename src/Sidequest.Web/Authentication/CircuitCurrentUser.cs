using Microsoft.AspNetCore.Components.Authorization;
using Sidequest.Application.Abstractions;

namespace Sidequest.Web.Authentication;

/// <summary>Resolves the signed-in identity from circuit authentication state rather than a captured HTTP request.</summary>
/// <param name="authenticationState">The scoped provider carrying the circuit's current principal.</param>
/// <param name="settings">The configured tenant and workforce admission requirements.</param>
/// <param name="clock">The UTC clock used to reject expired sessions.</param>
/// <remarks>Use within the owning circuit scope. Authentication-state access retains the caller's renderer context.</remarks>
public sealed class CircuitCurrentUser(
    AuthenticationStateProvider authenticationState, FoundationAuthenticationSettings settings, TimeProvider clock) : ICurrentUser
{
    /// <summary>Reads an unexpired principal that satisfies the configured workforce claims.</summary>
    /// <param name="cancellationToken">A token checked before requesting authentication state.</param>
    /// <returns>The tenant/object identity, or <see langword="null"/> for an expired or inadmissible principal.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested before reading state.</exception>
    /// <remarks>Resource policies must separately verify persisted account eligibility and resource access.</remarks>
    public async ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await authenticationState.GetAuthenticationStateAsync();
        return WorkforceSession.IsCurrent(state.User, clock.GetUtcNow()) ? WorkforceIdentity.Read(state.User, settings) : null;
    }
}
