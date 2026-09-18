using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Administration;

/// <summary>Reauthorizes global administration and performs audited, serializable assignment changes without content privileges.</summary>
/// <param name="factory">Short-lived operation contexts.</param>
/// <param name="access">Current persisted actor authorization.</param>
/// <param name="changes">Transactional notification outbox writer.</param>
/// <param name="clock">UTC audit and departure-proof clock.</param>
/// <param name="recoveryPolicy">Deployment-controlled external departure verification gate.</param>
public sealed class AdministrationService(ISidequestDbContextFactory factory, IResourceAccess access,
    IChangeWriter changes, TimeProvider clock, DepartureRecoveryPolicy recoveryPolicy)
{
    /// <summary>Lists same-tenant administrator assignments, including disabled assignments which confer no access.</summary>
    /// <param name="cancellationToken">Cancels authorization and SQL reads.</param>
    /// <param name="page">Optional one-based paging, default 25 and maximum 100 assignments per page.</param>
    /// <returns>Deterministically ordered minimal assignment records.</returns>
    /// <exception cref="DomainException">The current actor is not an eligible administrator (Forbidden).</exception>
    public async Task<IReadOnlyList<AdministratorSummary>> ListAsync(CancellationToken cancellationToken = default, PageRequest? page = null)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        page ??= new();
        return await (from assignment in db.Administrators.AsNoTracking()
                      join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
                      where user.TenantId == actor.TenantId
                      orderby user.DisplayName, user.Id
                      select new AdministratorSummary(user.Id, user.DisplayName,
                          user.IsEligible && user.DepartureVerifiedUtc == null, assignment.Version, user.Email))
            .Skip(page.Offset).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Selects at most 25 currently eligible, provisioned same-tenant accounts by trusted display name or email.</summary>
    /// <param name="query">At least two and at most 100 trimmed search characters; no arbitrary mailbox is accepted.</param>
    /// <param name="cancellationToken">Cancels authorization and SQL reads.</param>
    /// <returns>Minimal local account choices; directory provisioning remains the existing identity workflow.</returns>
    /// <exception cref="DomainException">The actor is forbidden or the search is invalid.</exception>
    public async Task<IReadOnlyList<AccountChoice>> SearchAccountsAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        query = query?.Trim() ?? "";
        if (query.Length is < 2 or > 100)
            throw new DomainException(ErrorCode.Validation, "Enter 2–100 search characters.", nameof(query));
        return await db.Users.AsNoTracking().Where(x => x.TenantId == actor.TenantId &&
                x.IsEligible && x.DepartureVerifiedUtc == null && (x.DisplayName.Contains(query) || x.Email.Contains(query)))
            .OrderBy(x => x.DisplayName).ThenBy(x => x.Id).Take(25)
            .Select(x => new AccountChoice(x.Id, x.DisplayName, x.Version, x.Email))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds an explicit administrator assignment without bootstrapping or restoring removed roles automatically.</summary>
    /// <param name="userId">Eligible local account selected through trusted same-tenant lookup.</param>
    /// <param name="accountVersion">Account rowversion captured by selection; changed eligibility/contact evidence causes Conflict.</param>
    /// <param name="cancellationToken">Cancels SQL work.</param>
    /// <returns>A task completing after assignment and audit commit atomically.</returns>
    /// <exception cref="DomainException">Authorization, eligibility, duplicate assignment or concurrency validation fails.</exception>
    public async Task AddAsync(Guid userId, byte[] accountVersion, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var target = await RequireTargetAsync(db, actor.TenantId, userId, cancellationToken).ConfigureAwait(false);
        CheckVersion(target.Version, accountVersion);
        if (await db.Administrators.AnyAsync(x => x.UserId == userId, cancellationToken).ConfigureAwait(false))
            throw new DomainException(ErrorCode.Conflict, "That account is already an administrator.");
        db.Administrators.Add(new Administrator { UserId = userId });
        Audit(db, ResourceKind.User, userId, actor.Id, "AdministratorAdded", "");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes an assignment while retaining an eligible same-tenant administrator even during competing removals.</summary>
    /// <param name="userId">Internal account whose explicit assignment is removed.</param>
    /// <param name="version">Assignment rowversion from the current administrator list.</param>
    /// <param name="cancellationToken">Cancels SQL work and invariant-lock waits.</param>
    /// <returns>A task completing after the assignment deletion and audit commit.</returns>
    /// <remarks>Serializable eligible-set reads hold key/range locks until commit. A competing removal may receive
    /// Conflict from SQL deadlock translation; no command retries using stale actor or version evidence.</remarks>
    /// <exception cref="DomainException">Actor/tenant authorization fails, no eligible administrator would remain, or a conflict occurs.</exception>
    public async Task RemoveAsync(Guid userId, byte[] version, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var eligibleIds = await (from assignment in db.Administrators
                                 join user in db.Users on assignment.UserId equals user.Id
                                 where user.TenantId == actor.TenantId && user.IsEligible && user.DepartureVerifiedUtc == null
                                 select user.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var assignmentToRemove = await db.Administrators.SingleOrDefaultAsync(x => x.UserId == userId &&
                db.Users.Any(u => u.Id == userId && u.TenantId == actor.TenantId), cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCode.NotFound, "Administrator assignment is unavailable.");
        CheckVersion(assignmentToRemove.Version, version);
        if (!eligibleIds.Any(x => x != userId))
            throw new DomainException(ErrorCode.Conflict, "At least one eligible administrator must remain.");
        db.Administrators.Remove(assignmentToRemove);
        Audit(db, ResourceKind.User, userId, actor.Id, "AdministratorRemoved", "");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks only the ownership-recovery preconditions for an explicitly addressed resource without returning content.</summary>
    /// <param name="kind">Event or Quest namespace.</param>
    /// <param name="resourceId">Exact resource supplied by the approved operational process.</param>
    /// <param name="cancellationToken">Cancels authorization and locking.</param>
    /// <returns>Content-free confirmation with a concurrency token.</returns>
    /// <exception cref="DomainException">The external gate is closed, any owner lacks verified departure, an eligible owner remains, or access fails.</exception>
    public async Task<RecoveryPreview> PreviewRecoveryAsync(ResourceKind kind, Guid resourceId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        RequireRecoveryGate();
        var eventId = await ResolveParentAsync(db, kind, resourceId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var evidence = await ReadEvidenceAsync(db, actor.TenantId, kind, resourceId, eventId, cancellationToken).ConfigureAwait(false);
        return new(kind, resourceId, evidence.Token);
    }

    /// <summary>Recovers only a verified ownerless resource; lifecycle/content, participation, following and private invitations remain unchanged.</summary>
    /// <param name="preview">Exact resource and concurrency evidence from content-free confirmation.</param>
    /// <param name="replacementId">Eligible same-tenant local account to receive the ownership assignment.</param>
    /// <param name="accountVersion">Current selected replacement account rowversion.</param>
    /// <param name="reason">Mandatory operational explanation, 10–2,000 trimmed characters; do not include private content.</param>
    /// <param name="cancellationToken">Cancels lock waits and transactional SQL operations.</param>
    /// <returns>A task completing after departed assignments, required individual membership, replacement ownership, audit and outbox commit.</returns>
    /// <exception cref="DomainException">Authorization, departure proof, external gate, input or concurrency requirements fail.</exception>
    public async Task RecoverAsync(RecoveryPreview preview, Guid replacementId, byte[] accountVersion,
        string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        RequireRecoveryGate();
        reason = reason?.Trim() ?? "";
        if (reason.Length is < 10 or > 2000)
            throw new DomainException(ErrorCode.Validation, "Provide a recovery reason of 10–2,000 characters.", nameof(reason));
        var eventId = await ResolveParentAsync(db, preview.Kind, preview.ResourceId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var evidence = await ReadEvidenceAsync(db, actor.TenantId, preview.Kind, preview.ResourceId, eventId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(evidence.Token, preview.Token, StringComparison.Ordinal))
            throw new DomainException(ErrorCode.Conflict, "Recovery evidence changed. Review the resource again; your inputs have not been saved.");
        var replacement = await RequireTargetAsync(db, actor.TenantId, replacementId, cancellationToken).ConfigureAwait(false);
        CheckVersion(replacement.Version, accountVersion);
        var now = clock.GetUtcNow();
        var membership = await db.EventMemberships.SingleOrDefaultAsync(x => x.EventId == eventId && x.UserId == replacementId,
            cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            membership = new EventMembership { EventId = eventId, UserId = replacementId };
            db.EventMemberships.Add(membership);
        }
        if (membership.Status != MembershipStatus.Active || membership.ChangedUtc == default)
        {
            membership.Status = MembershipStatus.Active;
            membership.ChangedById = actor.Id;
            membership.ChangedUtc = now;
        }
        if (preview.Kind == ResourceKind.Event)
        {
            db.EventOwners.RemoveRange(await db.EventOwners.Where(x => x.EventId == eventId).ToListAsync(cancellationToken).ConfigureAwait(false));
            db.EventOwners.Add(new EventOwner { EventId = eventId, UserId = replacementId });
        }
        else
        {
            db.QuestOwners.RemoveRange(await db.QuestOwners.Where(x => x.QuestId == preview.ResourceId).ToListAsync(cancellationToken).ConfigureAwait(false));
            db.QuestOwners.Add(new QuestOwner { QuestId = preview.ResourceId, UserId = replacementId });
        }
        var changeId = Guid.NewGuid();
        db.AuditEntries.Add(new AuditEntry
        {
            ResourceKind = preview.Kind,
            ResourceId = preview.ResourceId,
            ActorId = actor.Id,
            Action = "OwnershipRecovered",
            Reason = reason,
            CorrelationId = changeId.ToString("N"),
            OccurredUtc = now
        });
        db.AuditEntries.Add(new AuditEntry
        {
            ResourceKind = preview.Kind,
            ResourceId = preview.ResourceId,
            ActorId = actor.Id,
            Action = "DepartureVerificationProcedure",
            Reason = recoveryPolicy.ProcedureReference.Trim(),
            CorrelationId = changeId.ToString("N"),
            OccurredUtc = now
        });
        changes.Append(db, new ChangeEnvelope(changeId, NotificationKind.OwnershipChanged, eventId,
            preview.Kind == ResourceKind.Quest ? preview.ResourceId : null, actor.Id, [replacementId], now,
            Reason: "Ownership recovered through the approved departure procedure.", AffectedUserIds: [replacementId]));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RequireRecoveryGate()
    {
        if (!recoveryPolicy.Enabled || string.IsNullOrWhiteSpace(recoveryPolicy.ProcedureReference) ||
            recoveryPolicy.ProcedureReference.Length > 500 || recoveryPolicy.ProcedureReference.Any(char.IsControl))
            throw new DomainException(ErrorCode.DependencyUnavailable, "Ownership recovery requires an externally approved departure-verification procedure enabled by deployment operators.");
    }

    private static async Task<Guid> ResolveParentAsync(ISidequestDbContext db, ResourceKind kind, Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty || kind is not (ResourceKind.Event or ResourceKind.Quest))
            throw new DomainException(ErrorCode.Validation, "Specify one Event or Quest identifier.");
        return kind == ResourceKind.Event ? id :
            await db.Quests.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.EventId).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw Unavailable();
    }

    private async Task<RecoveryPreview> ReadEvidenceAsync(ISidequestDbContext db, Guid tenantId,
        ResourceKind kind, Guid resourceId, Guid eventId, CancellationToken ct)
    {
        var parent = await db.Events.AsNoTracking().Where(x => x.Id == eventId &&
            db.Users.Any(u => u.Id == x.CreatorId && u.TenantId == tenantId)).Select(x => x.Version).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw Unavailable();
        var resourceVersion = kind == ResourceKind.Event ? parent :
            await db.Quests.AsNoTracking().Where(x => x.Id == resourceId && x.EventId == eventId &&
                db.Users.Any(u => u.Id == x.CreatorId && u.TenantId == tenantId))
                .Select(x => x.Version).SingleOrDefaultAsync(ct).ConfigureAwait(false) ?? throw Unavailable();
        var owners = kind == ResourceKind.Event
            ? await db.EventOwners.AsNoTracking().Where(x => x.EventId == eventId).Select(x => new { x.UserId, x.Version }).ToListAsync(ct).ConfigureAwait(false)
            : await db.QuestOwners.AsNoTracking().Where(x => x.QuestId == resourceId).Select(x => new { x.UserId, x.Version }).ToListAsync(ct).ConfigureAwait(false);
        var ids = owners.Select(x => x.UserId).ToArray();
        var accounts = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct).ConfigureAwait(false);
        if (owners.Count == 0 || accounts.Count != owners.Count || accounts.Any(x => x.TenantId != tenantId ||
                x.DepartureVerifiedUtc == null || x.DepartureVerifiedUtc > clock.GetUtcNow()))
            throw new DomainException(ErrorCode.Conflict, "Recovery requires verified departure of every current owner. Otherwise use ordinary equal-owner management.");
        var facts = $"{kind}:{resourceId:N}:{Convert.ToHexString(parent)}:{Convert.ToHexString(resourceVersion)}:" +
            string.Join(";", owners.OrderBy(x => x.UserId).Select(x => $"{x.UserId:N}:{Convert.ToHexString(x.Version)}")) + ":" +
            string.Join(";", accounts.OrderBy(x => x.Id).Select(x => $"{x.Id:N}:{Convert.ToHexString(x.Version)}"));
        return new(kind, resourceId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts))));
    }

    private static async Task<UserAccount> RequireTargetAsync(ISidequestDbContext db, Guid tenantId, Guid id, CancellationToken ct) =>
        await db.Users.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.IsEligible &&
            x.DepartureVerifiedUtc == null, ct).ConfigureAwait(false)
        ?? throw new DomainException(ErrorCode.Validation, "Choose a currently eligible same-tenant local account.");

    private static void CheckVersion(byte[] actual, byte[]? expected)
    {
        if (expected is not { Length: > 0 } || !actual.SequenceEqual(expected))
            throw new DomainException(ErrorCode.Conflict, "The selected record changed. Reload before confirming; no changes were saved.");
    }

    private void Audit(ISidequestDbContext db, ResourceKind kind, Guid resourceId, Guid actorId, string action, string reason) =>
        db.AuditEntries.Add(new AuditEntry
        {
            ResourceKind = kind,
            ResourceId = resourceId,
            ActorId = actorId,
            Action = action,
            Reason = reason,
            CorrelationId = Guid.NewGuid().ToString("N"),
            OccurredUtc = clock.GetUtcNow()
        });

    private static DomainException Unavailable() => new(ErrorCode.NotFound, "Recovery resource is unavailable.");
}
