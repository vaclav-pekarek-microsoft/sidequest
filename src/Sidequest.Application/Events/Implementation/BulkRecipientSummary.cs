using Sidequest.Domain.Model;

namespace Sidequest.Application.Events.Implementation;

/// <summary>Owner-only per-individual outcome within an immutable bulk recipient snapshot.</summary>
/// <param name="User">Internal identity and display name, never an email roster field.</param>
/// <param name="Status">Current resumable application outcome.</param>
/// <param name="Detail">Safe operational explanation without provider response text.</param>
public sealed record BulkRecipientSummary(PersonSummary User, BulkRecipientStatus Status, string? Detail);
