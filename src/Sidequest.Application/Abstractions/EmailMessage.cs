namespace Sidequest.Application.Abstractions;

/// <summary>Single-recipient provider message; calendar payload and logical idempotency identity remain stable across retries.</summary>
/// <param name="Recipient">Trusted directory-resolved recipient address, never an arbitrary user-supplied destination.</param>
/// <param name="Subject">Rendered email subject.</param>
/// <param name="HtmlBody">Rendered HTML body with user data safely encoded.</param>
/// <param name="TextBody">Rendered plain-text alternative.</param>
/// <param name="IdempotencyKey">Stable logical delivery key reused for retries and provider deduplication where supported.</param>
/// <param name="CalendarContent">Recipient-only iCalendar payload, or null for a message without calendar content.</param>
/// <param name="CalendarMethod">iTIP method consistent with the calendar payload, or null when no calendar method is supplied.</param>
/// <param name="ReplyTo">Optional validated business response mailbox; never changes the trusted recipient, verified sender or calendar organizer.</param>
public sealed record EmailMessage(string Recipient, string Subject, string HtmlBody, string TextBody,
    string IdempotencyKey, string? CalendarContent = null, string? CalendarMethod = null, string? ReplyTo = null);
