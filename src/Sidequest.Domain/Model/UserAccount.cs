namespace Sidequest.Domain.Model;

/// <summary>Local account keyed externally by the Entra tenant/object pair, with independently checked eligibility.</summary>
public sealed class UserAccount : Entity
{
    /// <summary>Entra tenant identifier forming the external identity key with <see cref="ObjectId"/>.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Entra user object identifier, distinct from the inherited internal identifier.</summary>
    public Guid ObjectId { get; set; }
    /// <summary>Mutable directory display label; never an authorization key.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Trusted directory contact address; never an authorization key.</summary>
    public string Email { get; set; } = "";
    /// <summary>Whether the account satisfies the workforce eligibility policy; verified departure still denies access.</summary>
    public bool IsEligible { get; set; } = true;
    /// <summary>Most recent successful sign-in instant in UTC, or null before the first sign-in.</summary>
    public DateTimeOffset? LastSignedInUtc { get; set; }
    /// <summary>UTC instant of verified organizational departure, or null when departure has not been verified.</summary>
    public DateTimeOffset? DepartureVerifiedUtc { get; set; }
}
