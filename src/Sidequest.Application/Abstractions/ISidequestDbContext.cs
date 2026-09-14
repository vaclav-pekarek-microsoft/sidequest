using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Abstractions;

public interface ISidequestDbContext : IAsyncDisposable
{
    DbSet<UserAccount> Users { get; }
    DbSet<Administrator> Administrators { get; }
    DbSet<Event> Events { get; }
    DbSet<EventOwner> EventOwners { get; }
    DbSet<EventMembership> EventMemberships { get; }
    DbSet<EventMembershipRequest> MembershipRequests { get; }
    DbSet<EventInvitation> EventInvitations { get; }
    DbSet<Quest> Quests { get; }
    DbSet<QuestOwner> QuestOwners { get; }
    DbSet<QuestInvitation> QuestInvitations { get; }
    DbSet<QuestParticipation> Participations { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<EventStatusHistory> EventStatusHistory { get; }
    DbSet<QuestStatusHistory> QuestStatusHistory { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<NotificationPreference> NotificationPreferences { get; }
    DbSet<EventNotificationPreference> EventNotificationPreferences { get; }
    DbSet<NotificationTemplate> NotificationTemplates { get; }
    DbSet<ApplicationSetting> ApplicationSettings { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<ScheduledWork> ScheduledWork { get; }
    DbSet<NotificationDelivery> NotificationDeliveries { get; }
    DbSet<CalendarDeliveryState> CalendarDeliveryStates { get; }
    DbSet<MediaAsset> MediaAssets { get; }
    DbSet<BulkMembershipOperation> BulkOperations { get; }
    DbSet<BulkMembershipRecipient> BulkRecipients { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Serializable, CancellationToken cancellationToken = default);
}

public interface ISidequestDbContextFactory
{
    Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default);
}
