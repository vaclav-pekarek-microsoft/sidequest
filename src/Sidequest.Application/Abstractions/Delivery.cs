using Sidequest.Domain.Model;

namespace Sidequest.Application.Abstractions;

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
    Guid[]? PreviousAttendeeIds = null);

public interface IChangeWriter
{
    void Append(ISidequestDbContext db, ChangeEnvelope change);
}

public sealed record EmailMessage(string Recipient, string Subject, string HtmlBody, string TextBody,
    string IdempotencyKey, string? CalendarContent = null, string? CalendarMethod = null);
public sealed record EmailReceipt(string ProviderMessageId);

public interface IEmailGateway
{
    Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public interface IBackgroundWorkHandler
{
    string WorkType { get; }
    Task ExecuteAsync(Guid workId, CancellationToken cancellationToken);
}

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public int Offset => (Math.Max(1, Page) - 1) * Limit;
    public int Limit => Math.Clamp(PageSize, 1, 100);
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
