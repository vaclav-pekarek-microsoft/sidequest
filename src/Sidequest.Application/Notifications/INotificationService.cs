using Sidequest.Application.Abstractions;

namespace Sidequest.Application.Notifications;

/// <summary>Authorized recipient notification/preferences and narrowly scoped administrator delivery-recovery use cases.</summary>
/// <remarks>Implementations reauthorize eligible, non-departed actors using the database.
/// Mutations use operation-scoped, asynchronously disposed contexts and atomic state/audit/durable-work transactions.
/// External providers are never called inside SQL transactions; durable delivery attempts do not guarantee mailbox arrival.</remarks>
public interface INotificationService
{
    /// <summary>Lists the current recipient's in-app items after rechecking disclosure permissions.</summary>
    /// <param name="page">One-based paging input with page size 1 through 100.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>A privacy-filtered recipient page and matching authorized total count.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Identity/eligibility is forbidden or paging input is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<PageResult<NotificationSummary>> ListAsync(PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Counts unread items only within the current recipient's authorized notification scope.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of the count query.</param>
    /// <returns>The unread count, without exposing other recipients' items or protected resource existence.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current identity or eligibility is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default);
    /// <summary>Marks one or all of the current recipient's notifications read, without modifying other users' read state.</summary>
    /// <param name="notificationId">Internal item identifier, or null to mark all current recipient items read.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of read-state persistence.</param>
    /// <returns>A task completing after the repeat-safe read-state update is persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor is forbidden or the selected notification is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task MarkReadAsync(Guid? notificationId, CancellationToken cancellationToken = default);
    /// <summary>Reads the current user's optional notification preferences, using accepted defaults when no override is saved.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of preference retrieval.</param>
    /// <returns>Current preference values; mandatory service/calendar delivery is not configurable through them.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current identity or eligibility is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<PreferenceInput> GetPreferencesAsync(CancellationToken cancellationToken = default);
    /// <summary>Validates and saves the actor's optional delivery preferences, replacing obsolete reminder scheduling as needed.</summary>
    /// <param name="input">Preferences with a valid nullable IANA zone and 0.01–168-hour reminder lead time of at most two decimal places.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of preference and scheduling persistence.</param>
    /// <returns>A task completing after preferences and required durable scheduling changes are persisted without silent duration rounding.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Eligibility, zone, or reminder bounds/precision validation fails.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task SavePreferencesAsync(PreferenceInput input, CancellationToken cancellationToken = default);
    /// <summary>Sets the actor's Event-specific optional new-Quest email override without changing membership or mandatory delivery.</summary>
    /// <param name="eventId">Internal Event identifier subject to current resource authorization.</param>
    /// <param name="enabled">Whether optional new-Quest email is enabled for this Event instead of the user default.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of preference persistence.</param>
    /// <returns>A task completing after the per-Event override is persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor or Event access is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task SetEventNewQuestEmailAsync(Guid eventId, bool enabled, CancellationToken cancellationToken = default);
    /// <summary>Produces the current recipient-only invitation ICS as an authorized joined attendee's recovery download.</summary>
    /// <param name="questId">Internal Quest identifier; both Quest and Event must be Active and the Quest not ended.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of authorization and calendar generation.</param>
    /// <returns>iCalendar invitation text using UTC start/end and no tokens or private attendee roster; download does not replace durable updates/withdrawals.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, Joined participation, or lifecycle requirements fail, without disclosing unauthorized content.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<string> DownloadCalendarAsync(Guid questId, CancellationToken cancellationToken = default);
    /// <summary>Allows a current explicit administrator to inspect redacted durable delivery failures without granting general content access.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of failure retrieval.</param>
    /// <returns>Actionable failure summaries with stable record identifiers and replay discriminators.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current administrator authorization is absent (Forbidden).</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<DeliveryFailure>> FailedDeliveriesAsync(CancellationToken cancellationToken = default);
    /// <summary>Allows an administrator to durably request replay after correction, retaining the same logical delivery key and all current delivery guards.</summary>
    /// <param name="deliveryId">Internal failed record identifier obtained from the authorized failure view.</param>
    /// <param name="kind">Record-category discriminator matching the failure summary; not an arbitrary provider operation.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of replay scheduling.</param>
    /// <returns>A task completing after replay intent is persisted, not a promise of delivery; workers recheck access, preferences, lifecycle, and calendar ordering.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Administrator authorization, kind validation, record availability, or replay-state checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task ReplayAsync(Guid deliveryId, string kind, CancellationToken cancellationToken = default);
}
