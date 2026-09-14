using Sidequest.Application.Abstractions;

namespace Sidequest.Application.Events.Implementation;

/// <summary>Owner-only operational projections required for cancellation confirmation and actionable bulk outcomes.</summary>
/// <remarks>Complements the frozen IEventService without exposing Quest content, directory groups as access rules, or provider diagnostics.</remarks>
public interface IEventManagementQueries
{
    /// <summary>Counts child Quests currently affected by parent cancellation, including otherwise undisclosed Drafts.</summary>
    /// <param name="eventId">Event managed by the eligible current actor.</param>
    /// <param name="cancellationToken">Cancels the authorized query.</param>
    /// <returns>The number of Draft, Active, and Suspended child Quests, without any titles or rosters.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor is ineligible, ownership/membership is absent, or lifecycle no longer allows cancellation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation interrupts authorization or counting.</exception>
    public Task<int> GetCancellationImpactAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>Lists frozen recipients with safe, persisted outcomes for an owner-visible bulk operation.</summary>
    /// <param name="operationId">Internal one-time bulk operation ID.</param>
    /// <param name="page">Validated one-based pagination.</param>
    /// <param name="cancellationToken">Cancels the authorized query.</param>
    /// <returns>A deterministic page of identity names and applied/skipped/failed/pending outcomes, without email addresses.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Paging is invalid or the actor lacks eligible Event-owner access.</exception>
    /// <exception cref="OperationCanceledException">Cancellation interrupts authorization or paging.</exception>
    public Task<PageResult<BulkRecipientSummary>> ListBulkRecipientsAsync(Guid operationId, PageRequest page,
        CancellationToken cancellationToken = default);
}
