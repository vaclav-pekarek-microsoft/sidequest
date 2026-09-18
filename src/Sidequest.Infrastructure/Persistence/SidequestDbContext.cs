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
    public Task<UserAccount?> FindUserForUpdateAsync(Guid tenantId, Guid objectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tenantId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "A tenant identifier is required.", nameof(tenantId));
        if (objectId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An account object identifier is required.", nameof(objectId));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving an account identity.");

        return Users.FromSqlInterpolated($"""
            SELECT * FROM [Users] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_Users_TenantId_ObjectId]))
            WHERE [TenantId] = {tenantId} AND [ObjectId] = {objectId}
            """).AsTracking().SingleOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<QuestInvitation?> FindQuestInvitationForUpdateAsync(Guid questId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (questId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "A Quest identifier is required.", nameof(questId));
        if (userId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An account identifier is required.", nameof(userId));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving a Quest invitation.");

        return QuestInvitations.FromSqlInterpolated($"""
            SELECT * FROM [QuestInvitations] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_QuestInvitations_QuestId_UserId]))
            WHERE [QuestId] = {questId} AND [UserId] = {userId}
            """).AsTracking().SingleOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<QuestParticipation?> FindQuestParticipationForUpdateAsync(Guid questId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (questId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "A Quest identifier is required.", nameof(questId));
        if (userId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An account identifier is required.", nameof(userId));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving a Quest participation.");

        return Participations.FromSqlInterpolated($"""
            SELECT * FROM [Participations] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_Participations_QuestId_UserId]))
            WHERE [QuestId] = {questId} AND [UserId] = {userId}
            """).AsTracking().SingleOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<CalendarDeliveryState?> FindCalendarDeliveryStateForUpdateAsync(Guid questId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (questId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "A Quest identifier is required.", nameof(questId));
        if (userId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An account identifier is required.", nameof(userId));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving calendar intent.");

        return CalendarDeliveryStates.FromSqlInterpolated($"""
            SELECT * FROM [CalendarDeliveryStates] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_CalendarDeliveryStates_QuestId_UserId]))
            WHERE [QuestId] = {questId} AND [UserId] = {userId}
            """).AsTracking().SingleOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HasNotificationForUpdateAsync(Guid sourceChangeId, Guid userId, NotificationKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceChangeId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "A source change identifier is required.", nameof(sourceChangeId));
        if (userId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An account identifier is required.", nameof(userId));
        if (!Enum.IsDefined(kind))
            throw new DomainException(ErrorCode.Validation, "A defined notification kind is required.", nameof(kind));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving an inbox effect.");

        return Notifications.FromSqlInterpolated($"""
            SELECT * FROM [Notifications] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_Notifications_SourceChangeId_UserId_Kind]))
            WHERE [SourceChangeId] = {sourceChangeId} AND [UserId] = {userId} AND [Kind] = {(int)kind}
            """).AsNoTracking().AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<List<ScheduledWork>> ReadReminderSchedulesForUpdateAsync(Guid questId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (questId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "A Quest identifier is required.", nameof(questId));
        if (userId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An account identifier is required.", nameof(userId));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving reminder schedules.");

        var prefix = $"reminder:{questId:N}:{userId:N}:";
        return ScheduledWork.FromSqlInterpolated($"""
            SELECT * FROM [ScheduledWork] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_ScheduledWork_DeduplicationKey]))
            WHERE [DeduplicationKey] LIKE {prefix + "%"}
            """).AsTracking().ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task LockEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (eventId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An Event identifier is required.", nameof(eventId));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before locking an Event.");

        await Events.FromSqlInterpolated($"SELECT * FROM [Events] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {eventId}")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MembershipRequestState> ReadMembershipRequestStateForUpdateAsync(Guid eventId, Guid userId,
        DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (eventId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An Event identifier is required.", nameof(eventId));
        if (userId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An account identifier is required.", nameof(userId));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving membership requests.");

        // Use one covering, unfiltered range for both facts. Separate shared pending/history reads
        // can lock the clustered index's infinite gap and deadlock when different Events insert.
        return Database.SqlQuery<MembershipRequestState>($"""
            SELECT CAST(COALESCE(MAX(CASE WHEN [Status] = 0 THEN 1 ELSE 0 END), 0) AS bit) AS [HasPendingRequest],
                   COUNT(CASE WHEN [CreatedUtc] >= {since} THEN 1 END) AS [RecentRequestCount]
            FROM [MembershipRequests] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_MembershipRequests_EventId_UserId_CreatedUtc]))
            WHERE [EventId] = {eventId} AND [UserId] = {userId}
            """).SingleAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(SqlConflictCommandInterceptor.Instance, SqlConflictTransactionInterceptor.Instance);

    /// <inheritdoc />
    public Task<bool> HasScheduledWorkForUpdateAsync(string deduplicationKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(deduplicationKey) || deduplicationKey.Length > 300)
            throw new DomainException(ErrorCode.Validation, "A work key of 1 to 300 characters is required.", nameof(deduplicationKey));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving scheduled work.");

        return ScheduledWorkForUpdate().AnyAsync(x => x.DeduplicationKey == deduplicationKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HasPendingScheduledWorkForUpdateAsync(string deduplicationPrefix, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(deduplicationPrefix) || deduplicationPrefix.Length > 300)
            throw new DomainException(ErrorCode.Validation, "A work key prefix of 1 to 300 characters is required.", nameof(deduplicationPrefix));
        if (Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("An explicit Serializable transaction is required before reserving scheduled work.");

        return ScheduledWorkForUpdate().AnyAsync(x => x.DeduplicationKey.StartsWith(deduplicationPrefix) &&
            (x.Status == WorkStatus.Pending || x.Status == WorkStatus.Processing), cancellationToken);
    }

    private IQueryable<ScheduledWork> ScheduledWorkForUpdate() =>
        ScheduledWork.FromSqlRaw("SELECT * FROM [ScheduledWork] WITH (UPDLOCK, HOLDLOCK)").AsNoTracking();

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
        modelBuilder.Entity<EventMembershipRequest>().HasIndex(x => new { x.EventId, x.UserId, x.CreatedUtc })
            .IncludeProperties(x => x.Status);
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

    /// <summary>Saves tracked changes and accepts their tracked state without permitting audit/history mutation.</summary>
    /// <returns>The number of state entries written to the database.</returns>
    /// <exception cref="DomainException">History mutation, stale row versions, or duplicate keys cause a Conflict failure.</exception>
    /// <remarks>Saving neither commits a caller-owned transaction nor dispatches external notifications.</remarks>
    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    /// <summary>Saves tracked changes with uniform immutable-history and persistence-conflict enforcement.</summary>
    /// <param name="acceptAllChangesOnSuccess">Whether to accept tracked changes after the database write succeeds.</param>
    /// <returns>The number of state entries written to the database.</returns>
    /// <exception cref="DomainException">History mutation, stale row versions, or duplicate keys cause a Conflict failure.</exception>
    /// <remarks>When acceptance is disabled, the caller owns subsequent tracker acceptance. Other database failures propagate.</remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureHistoryIsImmutable();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    /// <summary>Saves tracked changes asynchronously and accepts their tracked state without permitting audit/history mutation.</summary>
    /// <param name="cancellationToken">Cancels the database write.</param>
    /// <returns>The number of state entries written to the database.</returns>
    /// <exception cref="DomainException">History mutation, stale row versions, or duplicate keys cause a Conflict failure.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed during the database write.</exception>
    /// <remarks>Saving neither commits a caller-owned transaction nor dispatches external notifications.</remarks>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    /// <summary>Saves tracked changes asynchronously with uniform immutable-history and persistence-conflict enforcement.</summary>
    /// <param name="acceptAllChangesOnSuccess">Whether to accept tracked changes after the database write succeeds.</param>
    /// <param name="cancellationToken">Cancels the database write.</param>
    /// <returns>The number of state entries written to the database.</returns>
    /// <exception cref="DomainException">History mutation, stale row versions, or duplicate keys cause a Conflict failure.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed during the database write.</exception>
    /// <remarks>When acceptance is disabled, the caller owns subsequent tracker acceptance. Other database failures propagate.</remarks>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureHistoryIsImmutable();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    private void EnsureHistoryIsImmutable()
    {
        if (ChangeTracker.Entries().Any(x =>
            (x.Entity is AuditEntry or Domain.Model.EventStatusHistory or Domain.Model.QuestStatusHistory or NotificationTemplate) &&
            (x.State is EntityState.Modified or EntityState.Deleted)))
        {
            throw new DomainException(ErrorCode.Conflict, "History records are immutable.");
        }
    }

}
