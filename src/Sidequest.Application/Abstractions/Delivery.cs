using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

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
    Guid[]? PreviousAttendeeIds = null,
    bool CalendarChanged = false,
    bool MaterialChange = false);

public static class WorkTypes
{
    public const string Change = "sidequest.change.v1";
    public const string EventCompletion = "event.complete.v1";
    public const string QuestCompletion = "quest.complete.v1";
    public const string BulkMembership = "event.bulk-membership.v1";
    public const string Reminder = "quest.reminder.v1";
}

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

    public int Limit => PageSize is >= 1 and <= 100 ? PageSize
        : throw new DomainException(ErrorCode.Validation, "Page size must be between 1 and 100.", "PageSize");
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
