namespace Sidequest.Application.Events;

/// <summary>Persisted contact identity disclosed only through an authorized roster or individual workflow.</summary>
/// <param name="Id">Internal account identifier, not an Entra object identifier.</param>
/// <param name="DisplayName">Directory-derived display label.</param>
/// <param name="Email">Persisted directory email for identifying the person without exposing account identifiers.</param>
public sealed record PersonSummary(Guid Id, string DisplayName, string Email)
{
    /// <summary>Identifies the authorized person by email and display name, never by the internal account identifier.</summary>
    public string Label => $"{Email} ({DisplayName})";
}
