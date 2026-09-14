using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Abstractions;

/// <summary>Per-operation EF Core unit of work for domain, audit, and durable-delivery records; asynchronously dispose after use.</summary>
/// <remarks>The caller owns explicit SQL transactions and commits state, audit, outbox, and schedule changes atomically.
/// Never perform Graph, email, or blob provider calls inside a SQL transaction. Do not retain a context for a UI circuit lifetime.</remarks>
public interface ISidequestDbContext : IAsyncDisposable
{
    /// <summary>Local accounts with external tenant/object keys and current eligibility.</summary>
    DbSet<UserAccount> Users { get; }
    /// <summary>Explicit global administrator assignments, without implicit content access.</summary>
    DbSet<Administrator> Administrators { get; }
    /// <summary>Event aggregates supplying local date windows and inherited Quest zones.</summary>
    DbSet<Event> Events { get; }
    /// <summary>Equal Event owner relations; no primary-owner rank exists.</summary>
    DbSet<EventOwner> EventOwners { get; }
    /// <summary>Authoritative individual Event memberships used for access checks.</summary>
    DbSet<EventMembership> EventMemberships { get; }
    /// <summary>Retained individual access requests, including pending and decided records.</summary>
    DbSet<EventMembershipRequest> MembershipRequests { get; }
    /// <summary>Consent-based Event invitations and their expiration/response states.</summary>
    DbSet<EventInvitation> EventInvitations { get; }
    /// <summary>Quest aggregates contained in parent Event windows.</summary>
    DbSet<Quest> Quests { get; }
    /// <summary>Equal Quest owner assignments separate from participation.</summary>
    DbSet<QuestOwner> QuestOwners { get; }
    /// <summary>Identity-bound private Quest access grants without an acceptance workflow.</summary>
    DbSet<QuestInvitation> QuestInvitations { get; }
    /// <summary>Exclusive None/Following/Joined state per Quest/user pair.</summary>
    DbSet<QuestParticipation> Participations { get; }
    /// <summary>Retained action audit records committed alongside domain changes.</summary>
    DbSet<AuditEntry> AuditEntries { get; }
    /// <summary>Retained Event lifecycle transitions with prior state and attribution.</summary>
    DbSet<EventStatusHistory> EventStatusHistory { get; }
    /// <summary>Retained Quest lifecycle transitions, including suspension history.</summary>
    DbSet<QuestStatusHistory> QuestStatusHistory { get; }
    /// <summary>Per-recipient in-app items whose content requires read-time authorization.</summary>
    DbSet<Notification> Notifications { get; }
    /// <summary>Per-user optional email and reminder choices.</summary>
    DbSet<NotificationPreference> NotificationPreferences { get; }
    /// <summary>Per-Event overrides of optional new-Quest email preferences.</summary>
    DbSet<EventNotificationPreference> EventNotificationPreferences { get; }
    /// <summary>Versioned, validated notification template overrides.</summary>
    DbSet<NotificationTemplate> NotificationTemplates { get; }
    /// <summary>Allowlisted business settings, excluding deployment secrets.</summary>
    DbSet<ApplicationSetting> ApplicationSettings { get; }
    /// <summary>Versioned durable changes staged atomically with domain state for later dispatch.</summary>
    DbSet<OutboxMessage> OutboxMessages { get; }
    /// <summary>Leased timed work for completion, reminders, and bulk workflows.</summary>
    DbSet<ScheduledWork> ScheduledWork { get; }
    /// <summary>Per-recipient durable delivery attempts, deduplication, and provider outcomes.</summary>
    DbSet<NotificationDelivery> NotificationDeliveries { get; }
    /// <summary>Per-Quest/recipient intended and sent calendar sequence state, including uncertain outcomes.</summary>
    DbSet<CalendarDeliveryState> CalendarDeliveryStates { get; }
    /// <summary>Private media references and readiness metadata protected by Quest access.</summary>
    DbSet<MediaAsset> MediaAssets { get; }
    /// <summary>One-time directory expansion workflows; source group IDs have no authorization role.</summary>
    DbSet<BulkMembershipOperation> BulkOperations { get; }
    /// <summary>Frozen individual expansion recipients and resumable per-recipient outcomes.</summary>
    DbSet<BulkMembershipRecipient> BulkRecipients { get; }
    /// <summary>Persists tracked changes without committing a caller-owned explicit transaction.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of database writes.</param>
    /// <returns>The number of state entries written to the database.</returns>
    /// <exception cref="DbUpdateException">The database rejects a tracked change.</exception>
    /// <exception cref="DbUpdateConcurrencyException">An optimistic concurrency conflict is detected.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    /// <summary>Begins a caller-owned explicit transaction for atomic domain, audit, and durable-work changes.</summary>
    /// <param name="isolationLevel">Database isolation level; Serializable is the default for invariant-preserving operations.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation while beginning the transaction.</param>
    /// <returns>A transaction the caller must commit on success or roll back on failure and asynchronously dispose.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Serializable, CancellationToken cancellationToken = default);
}

/// <summary>Creates isolated operation-scoped database contexts instead of sharing mutable EF state across requests or circuits.</summary>
public interface ISidequestDbContextFactory
{
    /// <summary>Creates a new context whose lifetime and explicit transactions belong to the caller.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of context creation.</param>
    /// <returns>A new context to asynchronously dispose after the operation; creation does not imply an active explicit transaction.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default);
}
