using Sidequest.Domain.Model;

namespace Sidequest.Application.Events;

/// <summary>Manager-visible progress for a one-time group expansion and individual audience operation.</summary>
/// <param name="Id">Internal bulk operation identifier.</param>
/// <param name="Mode">Direct add or consent-based invitation mode.</param>
/// <param name="Status">Overall expansion/application state.</param>
/// <param name="Total">Number of recorded snapshot recipients; interpret with Status while expansion is incomplete.</param>
/// <param name="Applied">Number of recipients whose individual operation was applied.</param>
/// <param name="Skipped">Number of recipients skipped by idempotency or current-state safeguards.</param>
/// <param name="Failed">Number of recipients with recorded failures.</param>
/// <param name="Error">Safe operation-level error, or null when none is recorded.</param>
public sealed record BulkOperationSummary(Guid Id, BulkMode Mode, BulkStatus Status,
    int Total, int Applied, int Skipped, int Failed, string? Error);
