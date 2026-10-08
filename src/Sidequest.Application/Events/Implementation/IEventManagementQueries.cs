using Sidequest.Application.Abstractions;

namespace Sidequest.Application.Events.Implementation;

/// <summary>Owner-only operational projections required for cancellation confirmation.</summary>
/// <remarks>Complements IEventService without exposing Quest content or provider diagnostics.</remarks>
public interface IEventManagementQueries
{
    /// <summary>Counts child Quests currently affected by parent cancellation, including otherwise undisclosed Drafts.</summary>
    /// <param name="eventId">Event managed by the eligible current actor.</param>
    /// <param name="cancellationToken">Cancels the authorized query.</param>
    /// <returns>The number of Draft, Active, and Suspended child Quests, without any titles or rosters.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor is ineligible, ownership/membership is absent, or lifecycle no longer allows cancellation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation interrupts authorization or counting.</exception>
    public Task<int> GetCancellationImpactAsync(Guid eventId, CancellationToken cancellationToken = default);
}
