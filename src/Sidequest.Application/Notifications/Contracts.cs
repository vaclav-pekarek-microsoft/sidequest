using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Notifications;

/// <summary>Current recipient's in-app projection, filtered so retained items cannot disclose protected content after access loss.</summary>
/// <param name="Id">Internal notification identifier.</param>
/// <param name="Kind">Business trigger determining notification policy.</param>
/// <param name="Summary">Safe current display text; access-loss notices must use minimal historical identifiers.</param>
/// <param name="EventId">Related Event identifier, or null when no Event reference is supplied.</param>
/// <param name="QuestId">Related Quest identifier, or null when no Quest reference is supplied; possession grants no resource access.</param>
/// <param name="CreatedUtc">UTC notification creation instant.</param>
/// <param name="IsRead">Whether the current recipient has marked the item read.</param>
public sealed record NotificationSummary(Guid Id, NotificationKind Kind, string Summary,
    Guid? EventId, Guid? QuestId, DateTimeOffset CreatedUtc, bool IsRead);
/// <summary>User-controlled optional delivery preferences; mandatory service and calendar messages remain enabled independently.</summary>
/// <param name="NewQuestEmail">Default opt-in for optional new public Quest email; accepted default is false.</param>
/// <param name="ActivityEmail">Whether optional joined/followed activity email is enabled; accepted default is true.</param>
/// <param name="RemindersEnabled">Whether attendee reminders are enabled for both email and in-app channels; accepted default is true.</param>
/// <param name="ReminderHours">Lead time from 0.01 through 168 hours, with at most two decimal places; accepted default is one hour.</param>
/// <param name="TimeZoneId">Preferred IANA display zone, or null to use browser zone then the inherited Quest zone; does not alter scheduling.</param>
public sealed record PreferenceInput(bool NewQuestEmail, bool ActivityEmail, bool RemindersEnabled,
    decimal ReminderHours, string? TimeZoneId);
/// <summary>Administrator-visible redacted durable-work failure available for investigation and authorized replay.</summary>
/// <param name="Id">Internal identifier of the failed delivery/work record.</param>
/// <param name="Kind">Replay discriminator identifying the durable record category, not a notification content authorization grant.</param>
/// <param name="Error">Redacted actionable failure detail without secrets or protected message bodies.</param>
/// <param name="Attempts">Recorded processing attempt count.</param>
/// <param name="DueUtc">UTC due instant recorded for the work; dead-letter status does not imply an automatic future retry.</param>
public sealed record DeliveryFailure(Guid Id, string Kind, string Error, int Attempts, DateTimeOffset DueUtc);

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
    Task<PageResult<NotificationSummary>> ListAsync(PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Counts unread items only within the current recipient's authorized notification scope.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of the count query.</param>
    /// <returns>The unread count, without exposing other recipients' items or protected resource existence.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current identity or eligibility is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<int> UnreadCountAsync(CancellationToken cancellationToken = default);
    /// <summary>Marks one or all of the current recipient's notifications read, without modifying other users' read state.</summary>
    /// <param name="notificationId">Internal item identifier, or null to mark all current recipient items read.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of read-state persistence.</param>
    /// <returns>A task completing after the repeat-safe read-state update is persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor is forbidden or the selected notification is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task MarkReadAsync(Guid? notificationId, CancellationToken cancellationToken = default);
    /// <summary>Reads the current user's optional notification preferences, using accepted defaults when no override is saved.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of preference retrieval.</param>
    /// <returns>Current preference values; mandatory service/calendar delivery is not configurable through them.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current identity or eligibility is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<PreferenceInput> GetPreferencesAsync(CancellationToken cancellationToken = default);
    /// <summary>Validates and saves the actor's optional delivery preferences, replacing obsolete reminder scheduling as needed.</summary>
    /// <param name="input">Preferences with a valid nullable IANA zone and 0.01–168-hour reminder lead time of at most two decimal places.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of preference and scheduling persistence.</param>
    /// <returns>A task completing after preferences and required durable scheduling changes are persisted without silent duration rounding.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Eligibility, zone, or reminder bounds/precision validation fails.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task SavePreferencesAsync(PreferenceInput input, CancellationToken cancellationToken = default);
    /// <summary>Sets the actor's Event-specific optional new-Quest email override without changing membership or mandatory delivery.</summary>
    /// <param name="eventId">Internal Event identifier subject to current resource authorization.</param>
    /// <param name="enabled">Whether optional new-Quest email is enabled for this Event instead of the user default.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of preference persistence.</param>
    /// <returns>A task completing after the per-Event override is persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor or Event access is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task SetEventNewQuestEmailAsync(Guid eventId, bool enabled, CancellationToken cancellationToken = default);
    /// <summary>Produces the current recipient-only invitation ICS as an authorized joined attendee's recovery download.</summary>
    /// <param name="questId">Internal Quest identifier; both Quest and Event must be Active and the Quest not ended.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of authorization and calendar generation.</param>
    /// <returns>iCalendar invitation text using UTC start/end and no tokens or private attendee roster; download does not replace durable updates/withdrawals.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, Joined participation, or lifecycle requirements fail, without disclosing unauthorized content.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<string> DownloadCalendarAsync(Guid questId, CancellationToken cancellationToken = default);
    /// <summary>Allows a current explicit administrator to inspect redacted durable delivery failures without granting general content access.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of failure retrieval.</param>
    /// <returns>Actionable failure summaries with stable record identifiers and replay discriminators.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current administrator authorization is absent (Forbidden).</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<DeliveryFailure>> FailedDeliveriesAsync(CancellationToken cancellationToken = default);
    /// <summary>Allows an administrator to durably request replay after correction, retaining the same logical delivery key and all current delivery guards.</summary>
    /// <param name="deliveryId">Internal failed record identifier obtained from the authorized failure view.</param>
    /// <param name="kind">Record-category discriminator matching the failure summary; not an arbitrary provider operation.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of replay scheduling.</param>
    /// <returns>A task completing after replay intent is persisted, not a promise of delivery; workers recheck access, preferences, lifecycle, and calendar ordering.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Administrator authorization, kind validation, record availability, or replay-state checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task ReplayAsync(Guid deliveryId, string kind, CancellationToken cancellationToken = default);
}
