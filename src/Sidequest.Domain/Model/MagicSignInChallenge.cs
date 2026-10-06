namespace Sidequest.Domain.Model;

/// <summary>Short-lived, single-use proof challenge for a normalized Microsoft mailbox.</summary>
public sealed class MagicSignInChallenge : Entity
{
    /// <summary>Normalized recipient address whose mailbox must receive the one-time code.</summary>
    public string Email { get; set; } = "";

    /// <summary>Random salt used by the bounded password-based code hash.</summary>
    public byte[] CodeSalt { get; set; } = [];

    /// <summary>Derived one-time-code hash; the plaintext code is never persisted.</summary>
    public byte[] CodeHash { get; set; } = [];

    /// <summary>UTC instant when the challenge was created.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>UTC instant at which verification must fail.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }

    /// <summary>Number of verification attempts already consumed.</summary>
    public int AttemptCount { get; set; }

    /// <summary>UTC instant when the challenge became unusable, or null while active.</summary>
    public DateTimeOffset? ConsumedUtc { get; set; }
}
