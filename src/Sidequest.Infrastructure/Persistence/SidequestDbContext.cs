using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Infrastructure.Persistence;

/// <summary>
/// Maps Sidequest's individual memberships, equal ownership, participation, and delivery
/// records to SQL Server, enforcing persistence uniqueness and optimistic concurrency.
/// </summary>
/// <remarks>
/// Create and dispose a context per application operation. Callers own transaction
/// boundaries; business changes, audit records, and outbox entries must commit together.
/// Do not retain a context for a Blazor circuit or call external providers inside its transaction.
/// Instances are not thread-safe; await each operation before reusing the same context.
/// </remarks>
/// <param name="options">The SQL Server provider and connection configuration for this context.</param>
public sealed class SidequestDbContext(DbContextOptions<SidequestDbContext> options)
    : DbContext(options), ISidequestDbContext
{
    /// <inheritdoc />
    public DbSet<UserAccount> Users => Set<UserAccount>();
    /// <inheritdoc />
    public DbSet<Administrator> Administrators => Set<Administrator>();
    /// <inheritdoc />
    public DbSet<Event> Events => Set<Event>();
    /// <inheritdoc />
    public DbSet<EventOwner> EventOwners => Set<EventOwner>();
    /// <inheritdoc />
    public DbSet<EventMembership> EventMemberships => Set<EventMembership>();
    /// <inheritdoc />
    public DbSet<EventMembershipRequest> MembershipRequests => Set<EventMembershipRequest>();
    /// <inheritdoc />
    public DbSet<EventInvitation> EventInvitations => Set<EventInvitation>();
    /// <inheritdoc />
    public DbSet<Quest> Quests => Set<Quest>();
    /// <inheritdoc />
    public DbSet<QuestOwner> QuestOwners => Set<QuestOwner>();
    /// <inheritdoc />
    public DbSet<QuestInvitation> QuestInvitations => Set<QuestInvitation>();
    /// <inheritdoc />
    public DbSet<QuestParticipation> Participations => Set<QuestParticipation>();
    /// <inheritdoc />
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    /// <inheritdoc />
    public DbSet<EventStatusHistory> EventStatusHistory => Set<EventStatusHistory>();
    /// <inheritdoc />
    public DbSet<QuestStatusHistory> QuestStatusHistory => Set<QuestStatusHistory>();
    /// <inheritdoc />
    public DbSet<Notification> Notifications => Set<Notification>();
    /// <inheritdoc />
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    /// <inheritdoc />
    public DbSet<EventNotificationPreference> EventNotificationPreferences => Set<EventNotificationPreference>();
    /// <inheritdoc />
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    /// <inheritdoc />
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
    /// <inheritdoc />
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    /// <inheritdoc />
    public DbSet<ScheduledWork> ScheduledWork => Set<ScheduledWork>();
    /// <inheritdoc />
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    /// <inheritdoc />
    public DbSet<CalendarDeliveryState> CalendarDeliveryStates => Set<CalendarDeliveryState>();
    /// <inheritdoc />
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    /// <inheritdoc />
    public DbSet<BulkMembershipOperation> BulkOperations => Set<BulkMembershipOperation>();
    /// <inheritdoc />
    public DbSet<BulkMembershipRecipient> BulkRecipients => Set<BulkMembershipRecipient>();

    /// <inheritdoc />
    public Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Serializable,
        CancellationToken cancellationToken = default) => Database.BeginTransactionAsync(isolationLevel, cancellationToken);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<Entity>();
        foreach (var entity in modelBuilder.Model.GetEntityTypes().ToArray())
        {
            var builder = modelBuilder.Entity(entity.ClrType);
            builder.HasKey(nameof(Entity.Id));
            builder.Property(nameof(Entity.Version)).IsRowVersion();
            foreach (var property in entity.GetProperties().Where(x => x.ClrType == typeof(string)))
                builder.Property(property.Name).HasMaxLength(10000);
        }

        modelBuilder.Entity<UserAccount>().HasIndex(x => new { x.TenantId, x.ObjectId }).IsUnique();
        modelBuilder.Entity<UserAccount>().Property(x => x.Email).HasMaxLength(320);
        modelBuilder.Entity<UserAccount>().Property(x => x.DisplayName).HasMaxLength(256);
        modelBuilder.Entity<Administrator>().HasIndex(x => x.UserId).IsUnique();
        modelBuilder.Entity<EventOwner>().HasIndex(x => new { x.EventId, x.UserId }).IsUnique();
        modelBuilder.Entity<EventMembership>().HasIndex(x => new { x.EventId, x.UserId }).IsUnique();
        modelBuilder.Entity<EventMembershipRequest>().HasIndex(x => new { x.EventId, x.UserId })
            .IsUnique().HasFilter("[Status] = 0");
        modelBuilder.Entity<EventInvitation>().HasIndex(x => new { x.EventId, x.UserId })
            .IsUnique().HasFilter("[Status] = 0");
        modelBuilder.Entity<QuestOwner>().HasIndex(x => new { x.QuestId, x.UserId }).IsUnique();
        modelBuilder.Entity<QuestInvitation>().HasIndex(x => new { x.QuestId, x.UserId }).IsUnique();
        modelBuilder.Entity<QuestParticipation>().HasIndex(x => new { x.QuestId, x.UserId }).IsUnique();
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.SourceChangeId, x.UserId, x.Kind }).IsUnique();
        modelBuilder.Entity<NotificationPreference>().HasIndex(x => x.UserId).IsUnique();
        modelBuilder.Entity<NotificationPreference>().Property(x => x.ReminderHours).HasPrecision(5, 2);
        modelBuilder.Entity<EventNotificationPreference>().HasIndex(x => new { x.EventId, x.UserId }).IsUnique();
        modelBuilder.Entity<CalendarDeliveryState>().HasIndex(x => new { x.QuestId, x.UserId }).IsUnique();
        modelBuilder.Entity<BulkMembershipRecipient>().HasIndex(x => new { x.OperationId, x.UserId }).IsUnique();
        modelBuilder.Entity<NotificationTemplate>().Property(x => x.Key).HasMaxLength(100);
        modelBuilder.Entity<NotificationTemplate>().HasIndex(x => new { x.Key, x.Revision }).IsUnique();
        modelBuilder.Entity<ApplicationSetting>().Property(x => x.Key).HasMaxLength(100);
        modelBuilder.Entity<ApplicationSetting>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<ScheduledWork>().Property(x => x.DeduplicationKey).HasMaxLength(300);
        modelBuilder.Entity<ScheduledWork>().HasIndex(x => x.DeduplicationKey).IsUnique();
        modelBuilder.Entity<NotificationDelivery>().Property(x => x.DeduplicationKey).HasMaxLength(300);
        modelBuilder.Entity<NotificationDelivery>().HasIndex(x => x.DeduplicationKey).IsUnique();
        modelBuilder.Entity<OutboxMessage>().HasIndex(x => new { x.Status, x.DueUtc });
        modelBuilder.Entity<ScheduledWork>().HasIndex(x => new { x.Status, x.DueUtc });
        modelBuilder.Entity<NotificationDelivery>().HasIndex(x => new { x.Status, x.DueUtc });
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.CreatedUtc });
        modelBuilder.Entity<Quest>().HasIndex(x => new { x.EventId, x.Status, x.StartUtc });

        modelBuilder.Entity<Event>().Property(x => x.Name).HasMaxLength(120);
        modelBuilder.Entity<Event>().Property(x => x.DiscoverySummary).HasMaxLength(300);
        modelBuilder.Entity<Event>().Property(x => x.TimeZoneId).HasMaxLength(100);
        modelBuilder.Entity<Quest>().Property(x => x.Title).HasMaxLength(120);
        modelBuilder.Entity<Quest>().Property(x => x.Location).HasMaxLength(500);
        modelBuilder.Entity<Quest>().ToTable(t => t.HasCheckConstraint("CK_Quest_Interval", "[EndUtc] > [StartUtc]"));
        modelBuilder.Entity<QuestParticipation>().ToTable(t => t.HasCheckConstraint("CK_Participation_State", "[Status] IN (0,1,2)"));
        modelBuilder.Entity<NotificationPreference>().ToTable(t =>
            t.HasCheckConstraint("CK_Preference_Hours", "[ReminderHours] >= 0.01 AND [ReminderHours] <= 168"));

        foreach (var type in new[] { typeof(Administrator), typeof(EventOwner), typeof(EventMembership),
            typeof(EventMembershipRequest), typeof(EventInvitation), typeof(QuestOwner), typeof(QuestInvitation),
            typeof(QuestParticipation), typeof(Notification), typeof(NotificationPreference),
            typeof(EventNotificationPreference), typeof(NotificationDelivery), typeof(CalendarDeliveryState),
            typeof(BulkMembershipRecipient) })
            modelBuilder.Entity(type).HasOne(typeof(UserAccount)).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Restrict);

        foreach (var type in new[] { typeof(EventOwner), typeof(EventMembership), typeof(EventMembershipRequest),
            typeof(EventInvitation), typeof(Quest), typeof(EventStatusHistory), typeof(EventNotificationPreference),
            typeof(BulkMembershipOperation) })
            modelBuilder.Entity(type).HasOne(typeof(Event)).WithMany().HasForeignKey("EventId").OnDelete(DeleteBehavior.Restrict);

        foreach (var type in new[] { typeof(QuestOwner), typeof(QuestInvitation), typeof(QuestParticipation),
            typeof(QuestStatusHistory), typeof(MediaAsset), typeof(CalendarDeliveryState) })
            modelBuilder.Entity(type).HasOne(typeof(Quest)).WithMany().HasForeignKey("QuestId").OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Event>().HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Quest>().HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<NotificationDelivery>().HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BulkMembershipRecipient>().HasOne<BulkMembershipOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OutboxMessage>().Property(x => x.PayloadJson).HasColumnType("nvarchar(max)").Metadata.SetMaxLength(null);
        modelBuilder.Entity<ScheduledWork>().Property(x => x.PayloadJson).HasColumnType("nvarchar(max)").Metadata.SetMaxLength(null);
        modelBuilder.Entity<NotificationDelivery>().Property(x => x.PayloadJson).HasColumnType("nvarchar(max)").Metadata.SetMaxLength(null);
        modelBuilder.Entity<CalendarDeliveryState>().Property(x => x.Payload).HasColumnType("nvarchar(max)").Metadata.SetMaxLength(null);
    }

    /// <summary>
    /// Saves tracked changes without permitting audit/history mutation, translating
    /// stale row versions and duplicate keys into explicit application conflicts.
    /// </summary>
    /// <param name="cancellationToken">Cancels the database write.</param>
    /// <returns>The number of state entries written to the database.</returns>
    /// <exception cref="DomainException">
    /// An audit/history record was modified or deleted, a row version is stale, or a unique key conflicts.
    /// </exception>
    /// <remarks>
    /// Saving does not commit a caller-owned transaction or dispatch external notifications.
    /// Other database failures propagate for the application boundary to report.
    /// </remarks>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries().Any(x =>
            (x.Entity is AuditEntry or Domain.Model.EventStatusHistory or Domain.Model.QuestStatusHistory) &&
            (x.State is EntityState.Modified or EntityState.Deleted)))
            throw new DomainException(ErrorCode.Conflict, "History records are immutable.");
        try
        {
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(ErrorCode.Conflict, "This item changed. Reload and try again.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            throw new DomainException(ErrorCode.Conflict, "This operation conflicts with a change already saved. Reload and try again.");
        }
    }
}
