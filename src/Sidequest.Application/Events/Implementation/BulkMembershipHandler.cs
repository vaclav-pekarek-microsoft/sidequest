using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Events.Implementation;

/// <summary>Freezes complete directory expansion before applying resumable, independently committed individual outcomes.</summary>
/// <remarks>The saved initiator is reauthorized for every recipient; groups never become access rules.
/// The dispatcher owns work leases and retry/dead-letter state. This handler persists truthful operational progress
/// and throws expansion/provider failures so they cannot be acknowledged as successful delivery.</remarks>
/// <param name="factory">Factory for fresh SQL units of work.</param>
/// <param name="directory">Complete, trusted workforce expansion outside SQL transactions.</param>
/// <param name="changes">Transactional change-envelope writer.</param>
/// <param name="quests">Caller-transaction Event completion cleanup.</param>
/// <param name="clock">Deterministic lifecycle and snapshot clock.</param>
/// <param name="options">Recipient and individual invitation limits.</param>
public sealed class BulkMembershipHandler(ISidequestDbContextFactory factory, IDirectoryGateway directory,
    IChangeWriter changes, IQuestEventLifecycle quests, TimeProvider clock, EventOperationOptions options) : IBackgroundWorkHandler
{
    /// <inheritdoc />
    public string WorkType => WorkTypes.BulkMembership;

    /// <inheritdoc />
    public async Task ExecuteAsync(Guid workId, CancellationToken cancellationToken)
    {
        options.Validate();
        BulkMembershipOperation operation;
        byte[] startedVersion;
        await using (var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false))
        {
            var work = await db.ScheduledWork.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId,
                cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
            var payload = EventTransactions.ReadPayload<BulkMembershipPayload>(work, WorkType);
            if (payload.SchemaVersion != 1 || payload.OperationId == Guid.Empty)
                throw new DomainException(ErrorCode.Validation, "Bulk membership payload is invalid or unsupported.");
            startedVersion = ReadStartedVersion(payload.StartedVersion);
            operation = await db.BulkOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == payload.OperationId,
                cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
            if (startedVersion.AsSpan().SequenceCompareTo(operation.Version) > 0)
                throw new DomainException(ErrorCode.Validation, "Bulk membership start version exceeds its persisted operation.");
            if (operation.Status == BulkStatus.Completed)
                return;
            if (!Enum.IsDefined(operation.Mode) || operation.SourceGroupId == Guid.Empty)
                throw new DomainException(ErrorCode.Validation, "The saved bulk operation is invalid.");
        }
        if (operation.SnapshotUtc is null)
        {
            try
            {
                await SnapshotAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException error)
            {
                await ExpansionFailedAsync(operation, error.Code, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        Guid[] unfinished;
        await using (var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false))
        {
            unfinished = await db.BulkRecipients.Where(x => x.OperationId == operation.Id &&
                x.Status == BulkRecipientStatus.Pending).OrderBy(x => x.Id).Select(x => x.Id)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var recipientId in unfinished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ApplyAsync(operation.Id, operation.EventId, recipientId, startedVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException error) when (error.Code is ErrorCode.Validation or ErrorCode.Forbidden or ErrorCode.NotFound or ErrorCode.Conflict)
            {
                await RecipientFailedAsync(operation, recipientId, error.Code, cancellationToken).ConfigureAwait(false);
            }
        }
        await FinishAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task SnapshotAsync(BulkMembershipOperation saved, CancellationToken cancellationToken)
    {
        await using (var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false))
        {
            await AuthorizeActorAsync(db, saved, cancellationToken).ConfigureAwait(false);
            var item = await db.Events.SingleAsync(x => x.Id == saved.EventId, cancellationToken).ConfigureAwait(false);
            EventTransactions.RequireActive(item, clock.GetUtcNow());
        }
        var expanded = await directory.ExpandGroupAsync(saved.SourceGroupId, cancellationToken).ConfigureAwait(false);
        var deduplicated = expanded.DistinctBy(x => (x.TenantId, x.ObjectId)).ToArray();
        if (deduplicated.Length > options.MaximumBulkRecipients)
            throw new DomainException(ErrorCode.Validation, "Group expansion exceeds the configured recipient limit.");
        if (expanded.GroupBy(x => (x.TenantId, x.ObjectId)).Any(x => x.Distinct().Count() != 1))
            throw new DomainException(ErrorCode.DependencyUnavailable, "Directory expansion returned conflicting identity records.");

        await using var target = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await target.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var itemToApply = await EventTransactions.LockAsync(target, saved.EventId, cancellationToken).ConfigureAwait(false);
        var operation = await target.BulkOperations.SingleAsync(x => x.Id == saved.Id, cancellationToken).ConfigureAwait(false);
        if (operation.SnapshotUtc is not null)
            return;
        var actor = await AuthorizeActorAsync(target, operation, cancellationToken).ConfigureAwait(false);
        EventTransactions.RequireActive(itemToApply, clock.GetUtcNow());
        foreach (var resolved in deduplicated)
        {
            if (resolved.TenantId != actor.TenantId || resolved.ObjectId == Guid.Empty)
                throw new DomainException(ErrorCode.DependencyUnavailable, "Directory expansion returned invalid tenant identities.");
            if (!resolved.IsEligible)
                continue;
            var existing = await target.Users.SingleOrDefaultAsync(x => x.TenantId == resolved.TenantId &&
                x.ObjectId == resolved.ObjectId, cancellationToken).ConfigureAwait(false);
            if (existing is not null && (!existing.IsEligible || existing.DepartureVerifiedUtc is not null))
            {
                target.BulkRecipients.Add(new BulkMembershipRecipient
                {
                    OperationId = saved.Id, UserId = existing.Id, Status = BulkRecipientStatus.Skipped,
                    Detail = "Local account is no longer eligible."
                });
                continue;
            }
            var user = await EventAudience.ResolveLocalAsync(target, resolved, actor.TenantId, cancellationToken).ConfigureAwait(false);
            target.BulkRecipients.Add(new BulkMembershipRecipient { OperationId = saved.Id, UserId = user.Id });
        }
        operation.SnapshotUtc = clock.GetUtcNow();
        operation.Status = BulkStatus.Applying;
        operation.LastError = null;
        EventTransactions.Audit(target, operation.EventId, operation.ActorId, "Bulk.SnapshotFrozen",
            $"Operation {operation.Id:N}; complete deduplicated directory enumeration frozen.", operation.SnapshotUtc.Value);
        await target.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAsync(Guid operationId, Guid eventId, Guid recipientId, byte[] startedVersion, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var item = await EventTransactions.LockAsync(db, eventId, cancellationToken).ConfigureAwait(false);
        var operation = await db.BulkOperations.SingleAsync(x => x.Id == operationId, cancellationToken).ConfigureAwait(false);
        var recipient = await db.BulkRecipients.SingleAsync(x => x.Id == recipientId && x.OperationId == operationId,
            cancellationToken).ConfigureAwait(false);
        if (recipient.Status != BulkRecipientStatus.Pending)
            return;
        var now = clock.GetUtcNow();
        var actor = await AuthorizeActorAsync(db, operation, cancellationToken).ConfigureAwait(false);
        if (await EventTransactions.CompleteAsync(db, item, quests, now, cancellationToken).ConfigureAwait(false))
        {
            recipient.Status = BulkRecipientStatus.Failed;
            recipient.Detail = "The Event ended before this recipient could be applied.";
        }
        else
        {
            EventTransactions.RequireActive(item, now);
            await EventAudience.EligibleAsync(db, recipient.UserId, actor.TenantId, cancellationToken).ConfigureAwait(false);
            var membership = await db.EventMemberships.SingleOrDefaultAsync(x => x.EventId == eventId &&
                x.UserId == recipient.UserId, cancellationToken).ConfigureAwait(false);
            var revocationVersions = await db.EventInvitations.Where(x => x.EventId == eventId &&
                x.UserId == recipient.UserId && x.Status != EventInvitationStatus.Pending &&
                x.Status != EventInvitationStatus.Accepted).Select(x => x.Version)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var laterRevocation = revocationVersions.Any(version => version.AsSpan().SequenceCompareTo(startedVersion) > 0);
            if (membership?.Status == MembershipStatus.Removed &&
                (operation.Mode == BulkMode.Add || membership.Version.AsSpan().SequenceCompareTo(startedVersion) > 0) || laterRevocation)
            {
                recipient.Status = BulkRecipientStatus.Skipped;
                recipient.Detail = "A removal or invitation response takes precedence; use an explicit individual action.";
            }
            else
            {
                var audience = new EventAudience(changes, options);
                var applied = operation.Mode == BulkMode.Add
                    ? await audience.ActivateAsync(db, item, recipient.UserId, actor.Id, false, now,
                        "An Event owner initiated a one-time individual bulk add.", NotificationKind.MembershipAdded,
                        cancellationToken).ConfigureAwait(false)
                    : await audience.InviteAsync(db, item, recipient.UserId, actor.Id, now, cancellationToken).ConfigureAwait(false);
                recipient.Status = applied ? BulkRecipientStatus.Applied : BulkRecipientStatus.Skipped;
                recipient.Detail = applied ? "Individual action applied." : "Already a member or already invited.";
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExpansionFailedAsync(BulkMembershipOperation saved, ErrorCode code, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(saved.EventId, cancellationToken).ConfigureAwait(false);
        var operation = await db.BulkOperations.SingleAsync(x => x.Id == saved.Id, cancellationToken).ConfigureAwait(false);
        if (operation.SnapshotUtc is null)
        {
            var firstFailure = operation.Status != BulkStatus.Failed;
            operation.Status = BulkStatus.Failed;
            operation.LastError = $"Expansion failed ({code}); no membership changes were applied. Retry after correction.";
            if (firstFailure)
                NotifyOutcome(db, operation, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecipientFailedAsync(BulkMembershipOperation saved, Guid recipientId, ErrorCode code,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(saved.EventId, cancellationToken).ConfigureAwait(false);
        var recipient = await db.BulkRecipients.SingleAsync(x => x.Id == recipientId && x.OperationId == saved.Id,
            cancellationToken).ConfigureAwait(false);
        if (recipient.Status == BulkRecipientStatus.Pending)
        {
            recipient.Status = BulkRecipientStatus.Failed;
            recipient.Detail = $"Individual action was not applied ({code}).";
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinishAsync(BulkMembershipOperation saved, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(saved.EventId, cancellationToken).ConfigureAwait(false);
        var operation = await db.BulkOperations.SingleAsync(x => x.Id == saved.Id, cancellationToken).ConfigureAwait(false);
        if (operation.Status == BulkStatus.Completed || operation.SnapshotUtc is null ||
            await db.BulkRecipients.AnyAsync(x => x.OperationId == saved.Id && x.Status == BulkRecipientStatus.Pending,
                cancellationToken).ConfigureAwait(false))
            return;
        var failed = await db.BulkRecipients.CountAsync(x => x.OperationId == saved.Id &&
            x.Status == BulkRecipientStatus.Failed, cancellationToken).ConfigureAwait(false);
        operation.Status = BulkStatus.Completed;
        operation.LastError = failed == 0 ? null : $"{failed} individuals could not be applied. Review outcomes before starting a new action.";
        NotifyOutcome(db, operation, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void NotifyOutcome(ISidequestDbContext db, BulkMembershipOperation operation, DateTimeOffset now)
    {
        var id = EventTransactions.Audit(db, operation.EventId, operation.ActorId, "Bulk.Outcome",
            $"Operation {operation.Id:N}; {operation.Status}.", now);
        changes.Append(db, new ChangeEnvelope(id, NotificationKind.BulkCompleted, operation.EventId, null,
            operation.ActorId, [operation.ActorId], now, Reason: operation.LastError ?? "One-time bulk operation completed."));
    }

    private static async Task<UserAccount> AuthorizeActorAsync(ISidequestDbContext db,
        BulkMembershipOperation operation, CancellationToken cancellationToken)
    {
        var savedActor = await db.Users.SingleOrDefaultAsync(x => x.Id == operation.ActorId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCode.Forbidden, "The initiating account is unavailable.");
        var access = new ResourceAccess(new SavedActorCurrentUser(
            new(savedActor.TenantId, savedActor.ObjectId, savedActor.DisplayName, savedActor.Email)));
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var item = await access.RequireEventAsync(db, operation.EventId, actor.Id, true, cancellationToken).ConfigureAwait(false);
        if (!await db.Users.AnyAsync(x => x.Id == item.CreatorId && x.TenantId == actor.TenantId, cancellationToken).ConfigureAwait(false) ||
            !await db.EventMemberships.AnyAsync(x => x.EventId == item.Id && x.UserId == actor.Id &&
                x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false))
            throw EventTransactions.Unavailable();
        return actor;
    }

    private static byte[] ReadStartedVersion(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            throw new DomainException(ErrorCode.Validation, "Bulk membership start version is invalid.");
        try
        {
            var version = Convert.FromBase64String(encoded);
            return version.Length == 8 ? version
                : throw new DomainException(ErrorCode.Validation, "Bulk membership start version is invalid.");
        }
        catch (FormatException)
        {
            throw new DomainException(ErrorCode.Validation, "Bulk membership start version is invalid.");
        }
    }
}
