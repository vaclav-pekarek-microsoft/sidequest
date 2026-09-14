using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Supplies a mutable request identity while recording authorization lookups and forwarded cancellation tokens.</summary>
/// <param name="identity">The initial tenant/object identity, or null for an unauthenticated caller.</param>
/// <remarks>This mutable fake is intentionally not thread-safe. Each scenario owns its instance and performs identity changes and recorded lookups sequentially.</remarks>
internal sealed class StubCurrentUser(UserIdentity? identity) : ICurrentUser
{
    /// <summary>Gets or sets the identity returned on the next lookup, allowing account changes on the same service instance.</summary>
    public UserIdentity? Identity { get; set; } = identity;
    /// <summary>Gets the number of completed identity lookups for this fake instance.</summary>
    public int Calls { get; private set; }
    /// <summary>Gets cancellation tokens in lookup order so tests can assert caller-token forwarding.</summary>
    public List<CancellationToken> ObservedTokens { get; } = [];

    /// <summary>Creates an identity bound to a persisted account's tenant/object pair but deliberately different contact fields.</summary>
    /// <param name="user">The account whose stable external identity the fake must represent.</param>
    /// <returns>A new fake that has not yet received an identity lookup.</returns>
    public static StubCurrentUser For(UserAccount user) => new(
        new UserIdentity(user.TenantId, user.ObjectId, "Identity display name", "identity@example.invalid"));

    /// <inheritdoc/>
    /// <remarks>Records the call and token, then returns the current fake identity without directory or network access.</remarks>
    public ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        ObservedTokens.Add(cancellationToken);
        return ValueTask.FromResult(Identity);
    }
}
