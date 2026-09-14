using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Abstractions;

/// <summary>Version-1 durable business change payload; outbox type/schema, aggregate, and correlation metadata live on the outbox record.</summary>
/// <param name="ChangeId">Stable application-generated change identifier reused for idempotent processing.</param>
/// <param name="Kind">Business trigger selecting recipient and delivery policy.</param>
/// <param name="EventId">Internal parent Event identifier.</param>
/// <param name="QuestId">Internal affected Quest identifier, or null for an Event-level change.</param>
/// <param name="ActorId">Internal acting account identifier, or null for a system change.</param>
/// <param name="RecipientIds">Captured internal recipient identifiers; publication fan-out may resolve and persist its registered-member set during processing.</param>
/// <param name="OccurredUtc">UTC instant of the source change, retained across retries.</param>
/// <param name="CalendarRevision">Transactionally allocated Quest calendar revision, or zero when no revision is supplied.</param>
/// <param name="Reason">Safe action explanation; empty when no reason is supplied.</param>
/// <param name="PreviousAttendeeIds">Prior attendee snapshot for withdrawal/transition handling, or null when not supplied.</param>
/// <param name="CalendarChanged">Optional flag indicating a calendar-relevant content change; defaults to false for payload compatibility.</param>
/// <param name="MaterialChange">Optional flag indicating a material attendee change requiring mandatory delivery; defaults to false.</param>
/// <remarks>Recipients remain subject to current access and optional preferences at delivery; minimal access-loss notices and withdrawals use restricted historical data.</remarks>
public sealed record ChangeEnvelope(
    Guid ChangeId,
    NotificationKind Kind,
    Guid EventId,
    Guid? QuestId,
    Guid? ActorId,
    Guid[] RecipientIds,
    DateTimeOffset OccurredUtc,
    long CalendarRevision = 0,
    string Reason = "",
    Guid[]? PreviousAttendeeIds = null,
    bool CalendarChanged = false,
    bool MaterialChange = false);

/// <summary>Stable versioned durable-work discriminators shared by producers and handlers.</summary>
public static class WorkTypes
{
    /// <summary>Version-1 business change envelope dispatched from the outbox.</summary>
    public const string Change = "sidequest.change.v1";
    /// <summary>Version-1 Event local-end completion work.</summary>
    public const string EventCompletion = "event.complete.v1";
    /// <summary>Version-1 Quest end-time completion work.</summary>
    public const string QuestCompletion = "quest.complete.v1";
    /// <summary>Version-1 one-time group expansion and individual membership/invitation work.</summary>
    public const string BulkMembership = "event.bulk-membership.v1";
    /// <summary>Version-1 attendee reminder work, deduplicated by user, Quest, and start revision.</summary>
    public const string Reminder = "quest.reminder.v1";
}

/// <summary>Stages durable change delivery in the same caller-owned unit of work as domain and audit changes.</summary>
public interface IChangeWriter
{
    /// <summary>Adds an outbox record to the supplied context without saving, committing, or calling external providers.</summary>
    /// <param name="db">Per-operation context whose explicit transaction is owned, saved, committed, and disposed by the caller.</param>
    /// <param name="change">Change to serialize; its stable identifier also identifies the outbox record.</param>
    void Append(ISidequestDbContext db, ChangeEnvelope change);
}

/// <summary>Single-recipient provider message; calendar payload and logical idempotency identity remain stable across retries.</summary>
/// <param name="Recipient">Trusted directory-resolved recipient address, never an arbitrary user-supplied destination.</param>
/// <param name="Subject">Rendered email subject.</param>
/// <param name="HtmlBody">Rendered HTML body with user data safely encoded.</param>
/// <param name="TextBody">Rendered plain-text alternative.</param>
/// <param name="IdempotencyKey">Stable logical delivery key reused for retries and provider deduplication where supported.</param>
/// <param name="CalendarContent">Recipient-only iCalendar payload, or null for a message without calendar content.</param>
/// <param name="CalendarMethod">iTIP method consistent with the calendar payload, or null when no calendar method is supplied.</param>
public sealed record EmailMessage(string Recipient, string Subject, string HtmlBody, string TextBody,
    string IdempotencyKey, string? CalendarContent = null, string? CalendarMethod = null);
/// <summary>Provider acceptance evidence, not a guarantee of recipient mailbox arrival.</summary>
/// <param name="ProviderMessageId">Provider-issued identifier to retain with durable delivery state.</param>
public sealed record EmailReceipt(string ProviderMessageId);

/// <summary>External email transport boundary invoked outside SQL transactions after durable delivery intent is committed.</summary>
public interface IEmailGateway
{
    /// <summary>Submits one recipient's message and exposes failure rather than reporting placeholder success.</summary>
    /// <param name="message">Authorized, rendered message with a stable logical delivery key.</param>
    /// <param name="cancellationToken">Cancels waiting/transport work; cancellation cannot prove the provider received nothing.</param>
    /// <returns>Provider acceptance receipt; mailbox delivery and exactly-once submission are not guaranteed.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed; the caller must retain any uncertain delivery outcome.</exception>
    Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Handler for durable leased work; implementations must tolerate replay and recheck applicable access and lifecycle rules.</summary>
public interface IBackgroundWorkHandler
{
    /// <summary>Versioned discriminator identifying the durable work this handler can process.</summary>
    string WorkType { get; }
    /// <summary>Processes the identified persisted work item without assuming exactly-once execution.</summary>
    /// <param name="workId">Internal identifier of the claimed durable work record.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation, including host shutdown; unfinished work must remain recoverable.</param>
    /// <returns>A task completing when handling finishes; faults must remain visible to retry/dead-letter orchestration.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed before handling completes.</exception>
    Task ExecuteAsync(Guid workId, CancellationToken cancellationToken);
}

/// <summary>One-based paging input validated when its database offset or limit is evaluated.</summary>
/// <param name="Page">One-based page number; must be positive and yield an offset within Int32.MaxValue.</param>
/// <param name="PageSize">Requested item count per page, inclusive range 1 through 100.</param>
public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    /// <summary>Zero-based number of rows to skip, calculated without integer overflow.</summary>
    /// <exception cref="DomainException">Page size is invalid, page is below one, or the calculated offset exceeds Int32.MaxValue (Validation).</exception>
    public int Offset
    {
        get
        {
            var offset = ((long)Page - 1) * Limit;
            if (Page < 1 || offset > int.MaxValue)
                throw new DomainException(ErrorCode.Validation, "Page is outside the supported range.", "Page");
            return (int)offset;
        }
    }

    /// <summary>Validated page size without silently clamping invalid input.</summary>
    /// <exception cref="DomainException">PageSize is outside 1 through 100 (Validation).</exception>
    public int Limit => PageSize is >= 1 and <= 100 ? PageSize
        : throw new DomainException(ErrorCode.Validation, "Page size must be between 1 and 100.", "PageSize");
}

/// <summary>Authorized paged projection with a total count over the same filtered result set.</summary>
/// <typeparam name="T">Projection type for each visible item.</typeparam>
/// <param name="Items">Visible items in the requested page; empty when no items match.</param>
/// <param name="TotalCount">Number of authorized matching items before paging, not a count of undisclosed resources.</param>
/// <param name="Page">One-based page number represented by the result.</param>
/// <param name="PageSize">Validated maximum number of items per page.</param>
public sealed record PageResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
