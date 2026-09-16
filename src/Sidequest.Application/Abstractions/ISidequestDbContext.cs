using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Abstractions;

/// <summary>Per-operation EF Core unit of work for domain, audit, and durable-delivery records; asynchronously dispose after use.</summary>
/// <remarks>The caller owns explicit SQL transactions and commits state, audit, outbox, and schedule changes atomically.
/// Never perform Graph, email, or blob provider calls inside a SQL transaction. Do not retain a context for a UI circuit lifetime.
/// Contexts and tracked entities are not thread-safe: await every operation before reusing the same instance,
/// and use separate contexts for independent concurrent operations.</remarks>
public interface ISidequestDbContext : IAsyncDisposable
{
    /// <summary>Local accounts with external tenant/object keys and current eligibility.</summary>
    public DbSet<UserAccount> Users { get; }
    /// <summary>Explicit global administrator assignments, without implicit content access.</summary>
    public DbSet<Administrator> Administrators { get; }
    /// <summary>Event aggregates supplying local date windows and inherited Quest zones.</summary>
    public DbSet<Event> Events { get; }
    /// <summary>Equal Event owner relations; no primary-owner rank exists.</summary>
    public DbSet<EventOwner> EventOwners { get; }
    /// <summary>Authoritative individual Event memberships used for access checks.</summary>
    public DbSet<EventMembership> EventMemberships { get; }
    /// <summary>Retained individual access requests, including pending and decided records.</summary>
    public DbSet<EventMembershipRequest> MembershipRequests { get; }
    /// <summary>Consent-based Event invitations and their expiration/response states.</summary>
    public DbSet<EventInvitation> EventInvitations { get; }
    /// <summary>Quest aggregates contained in parent Event windows.</summary>
    public DbSet<Quest> Quests { get; }
    /// <summary>Equal Quest owner assignments separate from participation.</summary>
    public DbSet<QuestOwner> QuestOwners { get; }
    /// <summary>Identity-bound private Quest access grants without an acceptance workflow.</summary>
    public DbSet<QuestInvitation> QuestInvitations { get; }
    /// <summary>Exclusive None/Following/Joined state per Quest/user pair.</summary>
    public DbSet<QuestParticipation> Participations { get; }
    /// <summary>Retained action audit records committed alongside domain changes.</summary>
    public DbSet<AuditEntry> AuditEntries { get; }
    /// <summary>Retained Event lifecycle transitions with prior state and attribution.</summary>
    public DbSet<EventStatusHistory> EventStatusHistory { get; }
    /// <summary>Retained Quest lifecycle transitions, including suspension history.</summary>
    public DbSet<QuestStatusHistory> QuestStatusHistory { get; }
    /// <summary>Per-recipient in-app items whose content requires read-time authorization.</summary>
    public DbSet<Notification> Notifications { get; }
    /// <summary>Per-user optional email and reminder choices.</summary>
    public DbSet<NotificationPreference> NotificationPreferences { get; }
    /// <summary>Per-Event overrides of optional new-Quest email preferences.</summary>
    public DbSet<EventNotificationPreference> EventNotificationPreferences { get; }
    /// <summary>Versioned, validated notification template overrides.</summary>
    public DbSet<NotificationTemplate> NotificationTemplates { get; }
    /// <summary>Allowlisted business settings, excluding deployment secrets.</summary>
    public DbSet<ApplicationSetting> ApplicationSettings { get; }
    /// <summary>Versioned durable changes staged atomically with domain state for later dispatch.</summary>
    public DbSet<OutboxMessage> OutboxMessages { get; }
    /// <summary>Leased timed work for completion, reminders, and bulk workflows.</summary>
    public DbSet<ScheduledWork> ScheduledWork { get; }
    /// <summary>Per-recipient durable delivery attempts, deduplication, and provider outcomes.</summary>
    public DbSet<NotificationDelivery> NotificationDeliveries { get; }
    /// <summary>Per-Quest/recipient intended and sent calendar sequence state, including uncertain outcomes.</summary>
    public DbSet<CalendarDeliveryState> CalendarDeliveryStates { get; }
    /// <summary>Private media references and readiness metadata protected by Quest access.</summary>
    public DbSet<MediaAsset> MediaAssets { get; }
    /// <summary>One-time directory expansion workflows; source group IDs have no authorization role.</summary>
    public DbSet<BulkMembershipOperation> BulkOperations { get; }
    /// <summary>Frozen individual expansion recipients and resumable per-recipient outcomes.</summary>
    public DbSet<BulkMembershipRecipient> BulkRecipients { get; }
    /// <summary>Persists tracked changes without committing a caller-owned explicit transaction.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of database writes.</param>
    /// <returns>The number of state entries written to the database.</returns>
    /// <exception cref="DomainException">An optimistic concurrency or duplicate-key conflict is translated to
    /// <see cref="ErrorCode.Conflict"/>, a SQL deadlock aborts the transaction, or an attempted modification/deletion of immutable audit or status-history
    /// records is rejected with that same code.</exception>
    /// <exception cref="DbUpdateException">Another database update failure occurs that is not translated to a domain conflict.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    /// <summary>Acquires the addressed Event's mutation lock for the caller's explicit Serializable transaction.</summary>
    /// <param name="eventId">Internal Event identifier resolved before beginning the mutation transaction.</param>
    /// <param name="cancellationToken">Cancels waiting for the database lock.</param>
    /// <returns>A task completing when the Event row or absent-key range is locked until transaction completion.</returns>
    /// <remarks>Call this before transactional actor, Quest, membership, or ownership reads. The lock neither
    /// grants access nor reports existence; reauthorize after acquisition. It does not track an Event,
    /// save changes, commit, or authorize external calls inside the transaction.</remarks>
    /// <exception cref="DomainException">The identifier is empty (Validation), or a competing database operation causes Conflict.</exception>
    /// <exception cref="InvalidOperationException">There is no caller-owned explicit Serializable transaction.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task LockEventAsync(Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>Reads pending and rate-limit facts while reserving the Event/account request range for a possible insertion.</summary>
    /// <param name="eventId">Nonempty Event identifier; acquire its mutation lock and authorize the caller first.</param>
    /// <param name="userId">Nonempty internal account identifier whose retained requests are counted.</param>
    /// <param name="since">Inclusive creation-time cutoff for counting requests in every status.</param>
    /// <param name="cancellationToken">Cancels the read or waiting for its write-intent reservation.</param>
    /// <returns>Pending existence independent of age and the number of requests created at or after <paramref name="since"/>.</returns>
    /// <remarks>The caller must own a Serializable transaction and read these facts before any shared request-history
    /// reads. The reservation lasts until transaction completion, including absent ranges. Adjacent absent ranges may
    /// contend. This method neither grants access nor tracks entities, saves, commits, or retries operations.</remarks>
    /// <exception cref="DomainException">An identifier is empty (Validation), or a competing database operation causes Conflict.</exception>
    /// <exception cref="InvalidOperationException">There is no caller-owned explicit Serializable transaction.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<MembershipRequestState> ReadMembershipRequestStateForUpdateAsync(Guid eventId, Guid userId,
        DateTimeOffset since, CancellationToken cancellationToken = default);
    /// <summary>Checks for pending or processing work while reserving the matching key range for a possible insertion.</summary>
    /// <param name="deduplicationPrefix">Literal nonblank key prefix, at most 300 characters; wildcard characters remain literal.</param>
    /// <param name="cancellationToken">Cancels the existence query or waiting for its write-intent range lock.</param>
    /// <returns>True when Pending or Processing work has the prefix; false otherwise, including terminal work.</returns>
    /// <remarks>Requires a caller-owned Serializable transaction. Acquire the parent Event lock first for
    /// Event/Quest mutations. The reservation lasts until transaction completion and avoids shared-read
    /// range conversion deadlocks. This method does not track entities, save, commit, or retry commands.</remarks>
    /// <exception cref="DomainException">The prefix is invalid (Validation), or a competing operation causes Conflict.</exception>
    /// <exception cref="InvalidOperationException">No caller-owned Serializable transaction exists.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<bool> HasPendingScheduledWorkForUpdateAsync(string deduplicationPrefix, CancellationToken cancellationToken = default);
    /// <summary>Begins a caller-owned explicit transaction for atomic domain, audit, and durable-work changes.</summary>
    /// <param name="isolationLevel">Database isolation level; Serializable is the default for invariant-preserving operations.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation while beginning the transaction.</param>
    /// <returns>A transaction the caller must commit on success or roll back on failure and asynchronously dispose.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Serializable, CancellationToken cancellationToken = default);
}
