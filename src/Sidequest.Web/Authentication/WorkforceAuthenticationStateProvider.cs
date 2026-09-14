using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Sidequest.Web.Authentication;

/// <summary>Periodically invalidates server circuits whose signed session or persisted account is no longer eligible.</summary>
/// <param name="loggerFactory">Creates the framework logger for revalidation and failure handling.</param>
/// <param name="scopes">Creates an independent service scope for each account revalidation.</param>
/// <remarks>The framework owns the revalidation loop and disposal. Do not share this scoped provider between circuits.</remarks>
public sealed class WorkforceAuthenticationStateProvider(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    /// <summary>Gets the one-minute interval between circuit eligibility checks.</summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    /// <remarks>A fresh scope checks SQL eligibility and signed session expiry; failures invalidate the circuit through the base provider.</remarks>
    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WorkforceAccounts>()
            .IsEligibleAsync(authenticationState.User, cancellationToken);
    }
}
