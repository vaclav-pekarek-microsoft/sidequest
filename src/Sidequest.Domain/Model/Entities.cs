namespace Sidequest.Domain.Model;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public byte[] Version { get; set; } = [];
}

public sealed class UserAccount : Entity
{
    public Guid TenantId { get; set; }
    public Guid ObjectId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsEligible { get; set; } = true;
    public DateTimeOffset? LastSignedInUtc { get; set; }
    public DateTimeOffset? DepartureVerifiedUtc { get; set; }
}

public sealed class Administrator : Entity
{
    public Guid UserId { get; set; }
}

public sealed class Event : Entity
{
    public Guid CreatorId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string DiscoverySummary { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string TimeZoneId { get; set; } = "Etc/UTC";
    public EventStatus Status { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class EventOwner : Entity
{
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class EventMembership : Entity
{
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
    public MembershipStatus Status { get; set; }
    public DateTimeOffset ChangedUtc { get; set; }
    public Guid ChangedById { get; set; }
}

public sealed class EventMembershipRequest : Entity
{
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
    public MembershipRequestStatus Status { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? DecidedUtc { get; set; }
    public Guid? DecidedById { get; set; }
}

public sealed class EventInvitation : Entity
{
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
    public Guid InvitedById { get; set; }
    public EventInvitationStatus Status { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? ResolvedUtc { get; set; }
}

public sealed class Quest : Entity
{
    public Guid EventId { get; set; }
    public Guid CreatorId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Location { get; set; } = "";
    public int? SuggestedCapacity { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public QuestVisibility Visibility { get; set; }
    public QuestStatus Status { get; set; }
    public string StatusReason { get; set; } = "";
    public Guid? CoverAssetId { get; set; }
    public long CalendarRevision { get; set; }
    public long StartRevision { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class QuestOwner : Entity
{
    public Guid QuestId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class QuestInvitation : Entity
{
    public Guid QuestId { get; set; }
    public Guid UserId { get; set; }
    public Guid InvitedById { get; set; }
    public QuestInvitationStatus Status { get; set; }
    public DateTimeOffset ChangedUtc { get; set; }
}

public sealed class QuestParticipation : Entity
{
    public Guid QuestId { get; set; }
    public Guid UserId { get; set; }
    public ParticipationStatus Status { get; set; }
    public DateTimeOffset ChangedUtc { get; set; }
}

public sealed class AuditEntry : Entity
{
    public ResourceKind ResourceKind { get; set; }
    public Guid ResourceId { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset OccurredUtc { get; set; }
}

public sealed class EventStatusHistory : Entity
{
    public Guid EventId { get; set; }
    public EventStatus Previous { get; set; }
    public EventStatus Next { get; set; }
    public Guid? ActorId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredUtc { get; set; }
}

public sealed class QuestStatusHistory : Entity
{
    public Guid QuestId { get; set; }
    public QuestStatus Previous { get; set; }
    public QuestStatus Next { get; set; }
    public Guid? ActorId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredUtc { get; set; }
}

public sealed class Notification : Entity
{
    public Guid UserId { get; set; }
    public Guid SourceChangeId { get; set; }
    public NotificationKind Kind { get; set; }
    public Guid? EventId { get; set; }
    public Guid? QuestId { get; set; }
    public string Summary { get; set; } = "";
    public bool IsAccessLossNotice { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? ReadUtc { get; set; }
}

public sealed class NotificationPreference : Entity
{
    public Guid UserId { get; set; }
    public bool NewQuestEmail { get; set; }
    public bool ActivityEmail { get; set; } = true;
    public bool RemindersEnabled { get; set; } = true;
    public decimal ReminderHours { get; set; } = 1;
    public string? TimeZoneId { get; set; }
}

public sealed class EventNotificationPreference : Entity
{
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
    public bool NewQuestEmail { get; set; }
}

public sealed class NotificationTemplate : Entity
{
    public string Key { get; set; } = "";
    public int Revision { get; set; }
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string TextBody { get; set; } = "";
    public DateTimeOffset ChangedUtc { get; set; }
    public Guid ChangedById { get; set; }
}

public sealed class ApplicationSetting : Entity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class OutboxMessage : Entity
{
    public string Type { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public Guid AggregateId { get; set; }
    public string PayloadJson { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset OccurredUtc { get; set; }
    public WorkStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset DueUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class ScheduledWork : Entity
{
    public string Type { get; set; } = "";
    public string DeduplicationKey { get; set; } = "";
    public Guid? QuestId { get; set; }
    public Guid? UserId { get; set; }
    public string PayloadJson { get; set; } = "";
    public WorkStatus Status { get; set; }
    public DateTimeOffset DueUtc { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class NotificationDelivery : Entity
{
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }
    public string DeduplicationKey { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public WorkStatus Status { get; set; }
    public DateTimeOffset DueUtc { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastError { get; set; }
}

public sealed class CalendarDeliveryState : Entity
{
    public Guid QuestId { get; set; }
    public Guid UserId { get; set; }
    public long IntendedSequence { get; set; }
    public long? SentSequence { get; set; }
    public string IntendedMethod { get; set; } = "";
    public string Payload { get; set; } = "";
    public bool MayHaveBeenDelivered { get; set; }
    public DateTimeOffset ChangedUtc { get; set; }
}

public sealed class MediaAsset : Entity
{
    public Guid QuestId { get; set; }
    public Guid CreatedById { get; set; }
    public string BlobName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public MediaStatus Status { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class BulkMembershipOperation : Entity
{
    public Guid EventId { get; set; }
    public Guid ActorId { get; set; }
    public Guid SourceGroupId { get; set; }
    public BulkMode Mode { get; set; }
    public BulkStatus Status { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? SnapshotUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class BulkMembershipRecipient : Entity
{
    public Guid OperationId { get; set; }
    public Guid UserId { get; set; }
    public BulkRecipientStatus Status { get; set; }
    public string? Detail { get; set; }
}
