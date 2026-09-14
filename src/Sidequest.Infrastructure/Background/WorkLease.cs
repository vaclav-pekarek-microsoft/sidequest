namespace Sidequest.Infrastructure.Background;

/// <summary>Opaque ownership proof returned by an atomic SQL claim; completion must match both token and unexpired lease.</summary>
/// <param name="Id">Durable row identity.</param>
/// <param name="Category">Allowlisted table category: outbox, scheduled, or delivery.</param>
/// <param name="Type">Versioned handler discriminator, or delivery for transport work.</param>
/// <param name="Token">Fresh claim ownership token.</param>
/// <param name="Attempts">Attempt number after claiming.</param>
public sealed record WorkLease(Guid Id, string Category, string Type, Guid Token, int Attempts);
