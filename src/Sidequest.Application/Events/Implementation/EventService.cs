using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Events.Implementation;

/// <summary>Implements server-authorized Event workflows with per-operation SQL transactions and explicit individual consent.</summary>
/// <remarks>All aggregate mutations acquire the Event update lock before authorization and child work.
/// Directory resolution precedes SQL transactions; every selected identity is revalidated inside the transaction.
/// Instances retain no EF context or protected resource state and may be scoped to an interactive circuit.</remarks>
public sealed class EventService : IEventService, IEventManagementQueries
{
    private readonly ISidequestDbContextFactory factory;
    private readonly IResourceAccess access;
    private readonly IDirectoryGateway directory;
    private readonly IQuestEventLifecycle quests;
    private readonly IChangeWriter changes;
    private readonly TimeProvider clock;
    private readonly EventOperationOptions options;
    private readonly EventAudience audience;
    private static readonly ConcurrentDictionary<Guid, SearchWindow> SearchWindows = new();

    /// <summary>Builds the Event application boundary without opening contexts or contacting providers.</summary>
    /// <param name="factory">Factory for operation-scoped SQL contexts.</param>
    /// <param name="access">Current identity and persisted resource authorization.</param>
    /// <param name="directory">Trusted, configured-tenant directory resolution outside transactions.</param>
    /// <param name="quests">Quest-owned transactional parent lifecycle and access cleanup.</param>
    /// <param name="changes">Appends durable, versioned delivery intent in the caller context.</param>
    /// <param name="clock">Clock for effective lifecycle, expiry, and rate-limit boundaries.</param>
    /// <param name="options">Configured positive workflow limits.</param>
    public EventService(ISidequestDbContextFactory factory, IResourceAccess access, IDirectoryGateway directory,
        IQuestEventLifecycle quests, IChangeWriter changes, TimeProvider clock, EventOperationOptions options)
    {
        options.Validate();
        this.factory = factory;
        this.access = access;
        this.directory = directory;
        this.quests = quests;
        this.changes = changes;
        this.clock = clock;
        this.options = options;
        audience = new EventAudience(changes, options);
    }

    /// <inheritdoc />
    public async Task<PageResult<EventSummary>> ListAsync(EventListKind kind, PageRequest page,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException(ErrorCode.Validation, "Choose a valid Event list.", "Kind");
        var offset = page.Offset;
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var memberships = db.EventMemberships.Where(x => x.UserId == actor.Id && x.Status == MembershipStatus.Active);
        var query = db.Events.AsNoTracking().Where(x => db.Users.Any(u => u.Id == x.CreatorId && u.TenantId == actor.TenantId)).Where(x =>
            x.Status == EventStatus.Draft || db.EventStatusHistory.Any(h => h.EventId == x.Id &&
                h.Previous == EventStatus.Draft && h.Next == EventStatus.Cancelled)
                ? memberships.Any(m => m.EventId == x.Id) && db.EventOwners.Any(o => o.EventId == x.Id && o.UserId == actor.Id)
                : x.Status == EventStatus.Active || memberships.Any(m => m.EventId == x.Id));
        var candidates = await query.OrderBy(x => x.StartDate).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var myIds = await memberships.Select(x => x.EventId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var mine = myIds.ToHashSet();
        var now = clock.GetUtcNow();
        var visible = candidates.Where(item =>
        {
            var status = EventTransactions.Effective(item, now);
            return kind switch
            {
                EventListKind.Available => status == EventStatus.Active && !mine.Contains(item.Id),
                EventListKind.History => mine.Contains(item.Id) && status is EventStatus.Completed or EventStatus.Cancelled or EventStatus.Archived,
                _ => mine.Contains(item.Id) && status is EventStatus.Draft or EventStatus.Active
            };
        }).ToList();
        var result = new List<EventSummary>();
        foreach (var item in visible.Skip(offset).Take(page.Limit))
            result.Add(await EventQueries.SummaryAsync(db, item, actor.Id, now, cancellationToken).ConfigureAwait(false));
        return new(result, visible.Count, page.Page, page.Limit);
    }

    /// <inheritdoc />
    public async Task<EventDetail> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var item = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false)
            ?? throw EventTransactions.Unavailable();
        await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
        var summary = await EventQueries.SummaryAsync(db, item, actor.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (summary.IsMember)
            await access.RequireEventAsync(db, id, actor.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(summary, summary.IsMember ? item.Description : null);
    }

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(EventInput input, CancellationToken cancellationToken = default)
    {
        var normalized = Validate(input);
        var eventId = Guid.NewGuid();
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var item = new Event { Id = eventId, CreatorId = actor.Id, CreatedUtc = now, UpdatedUtc = now };
        Copy(item, normalized);
        db.Events.Add(item);
        db.EventOwners.Add(new EventOwner { EventId = item.Id, UserId = actor.Id });
        db.EventMemberships.Add(new EventMembership
        {
            EventId = item.Id, UserId = actor.Id, Status = MembershipStatus.Active,
            ChangedById = actor.Id, ChangedUtc = now
        });
        EventTransactions.Audit(db, item.Id, actor.Id, "Event.Created", "Creator assigned as equal owner and member.", now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return item.Id;
    }

    /// <inheritdoc />
    public async Task EditAsync(Guid id, string version, EventInput input, CancellationToken cancellationToken = default)
    {
        var normalized = Validate(input);
        await MutateAsync(id, true, async (db, item, actor, now) =>
        {
            InputRules.Version(item, version);
            EventTransactions.RequireEditable(item, now);
            if (item.Status != EventStatus.Draft && item.TimeZoneId != normalized.TimeZoneId)
                throw new DomainException(ErrorCode.Validation, "The time zone is fixed after publication.", "TimeZoneId");
            var window = TimeRules.EventWindow(normalized.StartDate, normalized.EndDate, normalized.TimeZoneId);
            if (window.End <= now)
                throw new DomainException(ErrorCode.Validation, "The Event must not have ended.", "EndDate");
            if (await db.Quests.AnyAsync(x => x.EventId == id && x.Status != QuestStatus.Cancelled &&
                (x.StartUtc < window.Start || x.EndUtc > window.End), cancellationToken).ConfigureAwait(false))
                throw new DomainException(ErrorCode.Validation, "These dates would exclude an existing Quest.", "EndDate");
            Copy(item, normalized);
            item.UpdatedUtc = now;
            EventTransactions.Audit(db, id, actor.Id, "Event.Edited", "Event configuration updated.", now);
            if (item.Status == EventStatus.Active)
                await EventTransactions.ScheduleCompletionAsync(db, item, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ChangeStatusAsync(Guid id, string version, EventStatus target, string reason,
        CancellationToken cancellationToken = default) =>
        MutateAsync(id, true, async (db, item, actor, now) =>
        {
            InputRules.Version(item, version);
            switch (item.Status, target)
            {
                case (EventStatus.Draft, EventStatus.Active):
                    Validate(new(item.Name, item.Description, item.DiscoverySummary, item.StartDate, item.EndDate, item.TimeZoneId));
                    EventTransactions.RequireEditable(item, now);
                    if (!await EligibleOwnerExistsAsync(db, id, null, cancellationToken).ConfigureAwait(false))
                        throw EventTransactions.Conflict("An eligible owner with individual membership is required.");
                    EventTransactions.Transition(db, item, target, actor.Id, "Event published.", now);
                    await EventTransactions.ScheduleCompletionAsync(db, item, cancellationToken).ConfigureAwait(false);
                    var members = await (from member in db.EventMemberships
                                         join user in db.Users on member.UserId equals user.Id
                                         where member.EventId == id && member.Status == MembershipStatus.Active &&
                                               user.IsEligible && user.DepartureVerifiedUtc == null && user.Id != actor.Id
                                         select user.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (members.Length > 0)
                        Emit(db, id, actor.Id, "Audience.Published", NotificationKind.MembershipAdded, members,
                            "The Event has been published; your configured membership is now available.", now, members);
                    break;
                case (EventStatus.Draft or EventStatus.Active, EventStatus.Cancelled):
                    reason = InputRules.Reason(reason);
                    var published = item.Status == EventStatus.Active;
                    // Capture affected child audiences before the cascade changes their status or participation.
                    var cancellationRecipients = published
                        ? await EventAudience.CancellationRecipientsAsync(db, id, actor.TenantId, now, cancellationToken).ConfigureAwait(false)
                        : [];
                    await quests.CancelForEventAsync(db, id, actor.Id, reason, now, cancellationToken).ConfigureAwait(false);
                    await EventTransactions.ClosePendingAsync(db, item, null, now,
                        "The Event was cancelled and is no longer accepting membership requests.", cancellationToken).ConfigureAwait(false);
                    EventTransactions.Transition(db, item, target, actor.Id, reason, now);
                    if (published)
                        Emit(db, id, actor.Id, "Event.CancellationDelivery", NotificationKind.EventCancelled, cancellationRecipients, reason, now);
                    break;
                case (EventStatus.Completed, EventStatus.Archived):
                    if (await db.Quests.AnyAsync(x => x.EventId == id &&
                        x.Status != QuestStatus.Completed && x.Status != QuestStatus.Cancelled &&
                        x.Status != QuestStatus.Archived, cancellationToken).ConfigureAwait(false))
                        throw EventTransactions.Conflict("Complete or cancel all child Quests before archiving.");
                    EventTransactions.Transition(db, item, target, actor.Id, "Event archived.", now);
                    break;
                case (EventStatus.Cancelled, EventStatus.Archived):
                    EventTransactions.Transition(db, item, target, actor.Id, "Event archived.", now);
                    break;
                default:
                    throw EventTransactions.Conflict("This Event lifecycle transition is not allowed.");
            }
        }, cancellationToken);

    /// <inheritdoc />
    public Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default) =>
        MutateAsync(id, true, async (db, item, actor, now) =>
        {
            InputRules.Version(item, version);
            if (item.Status != EventStatus.Draft ||
                await db.MembershipRequests.AnyAsync(x => x.EventId == id, cancellationToken).ConfigureAwait(false) ||
                await db.EventInvitations.AnyAsync(x => x.EventId == id, cancellationToken).ConfigureAwait(false) ||
                await db.Quests.AnyAsync(x => x.EventId == id, cancellationToken).ConfigureAwait(false) ||
                await db.EventStatusHistory.AnyAsync(x => x.EventId == id, cancellationToken).ConfigureAwait(false))
                throw EventTransactions.Conflict("Only an unpublished Draft without requests, invitations, or Quests can be deleted.");
            db.EventOwners.RemoveRange(await db.EventOwners.Where(x => x.EventId == id).ToListAsync(cancellationToken).ConfigureAwait(false));
            db.EventMemberships.RemoveRange(await db.EventMemberships.Where(x => x.EventId == id).ToListAsync(cancellationToken).ConfigureAwait(false));
            db.EventNotificationPreferences.RemoveRange(await db.EventNotificationPreferences.Where(x => x.EventId == id)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
            db.Events.Remove(item);
            EventTransactions.Audit(db, id, actor.Id, "Event.Deleted", "Unpublished Draft deleted.", now);
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DuplicateEvent>> FindDuplicatesAsync(string name, DateOnly start, DateOnly end,
        CancellationToken cancellationToken = default)
    {
        name = InputRules.Text(name, "Name", 3, 120);
        if (end < start)
            throw new DomainException(ErrorCode.Validation, "End date must not precede start date.", "EndDate");
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var candidates = await db.Events.AsNoTracking().Where(x => db.Users.Any(u => u.Id == x.CreatorId && u.TenantId == actor.TenantId) &&
            x.Status == EventStatus.Active &&
            x.StartDate <= end && x.EndDate >= start).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var matches = candidates.Where(x => EventTransactions.Effective(x, now) == EventStatus.Active)
            .Select(x => (Item: x, Score: EventQueries.Similarity(name, x.Name)))
            .Where(x => x.Score >= 0.6).OrderByDescending(x => x.Score).ThenBy(x => x.Item.Id).Take(5);
        var result = new List<DuplicateEvent>();
        foreach (var match in matches)
            result.Add(new(await EventQueries.SummaryAsync(db, match.Item, actor.Id, now, cancellationToken).ConfigureAwait(false), match.Score));
        return result;
    }

    /// <inheritdoc />
    public Task RequestMembershipAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        MutateAsync(eventId, null, async (db, item, actor, now) =>
        {
            if (await db.EventStatusHistory.AnyAsync(x => x.EventId == eventId &&
                x.Previous == EventStatus.Draft && x.Next == EventStatus.Cancelled, cancellationToken).ConfigureAwait(false) &&
                !await db.EventOwners.AnyAsync(x => x.EventId == eventId && x.UserId == actor.Id, cancellationToken).ConfigureAwait(false))
                throw EventTransactions.Unavailable();
            EventTransactions.RequireActive(item, now);
            if (await db.EventMemberships.AnyAsync(x => x.EventId == eventId && x.UserId == actor.Id &&
                x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false))
                return;
            var since = now.AddHours(-1);
            var requests = await db.ReadMembershipRequestStateForUpdateAsync(eventId, actor.Id, since, cancellationToken).ConfigureAwait(false);
            if (requests.HasPendingRequest)
                return;
            if (requests.RecentRequestCount >= options.RequestsPerHour)
                throw EventTransactions.Conflict("The request rate limit was reached. Try again later.");
            db.MembershipRequests.Add(new EventMembershipRequest { EventId = eventId, UserId = actor.Id, CreatedUtc = now });
            Emit(db, eventId, actor.Id, "Membership.Requested", NotificationKind.MembershipRequested,
                await EventAudience.OwnerIdsAsync(db, eventId, cancellationToken).ConfigureAwait(false), "", now);
        }, cancellationToken);

    /// <inheritdoc />
    public async Task WithdrawRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var eventId = await RequestEventAsync(requestId, cancellationToken).ConfigureAwait(false);
        await MutateAsync(eventId, null, async (db, item, actor, now) =>
        {
            var request = await db.MembershipRequests.SingleOrDefaultAsync(x => x.Id == requestId &&
                x.UserId == actor.Id, cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
            if (request.Status == MembershipRequestStatus.Withdrawn)
                return;
            if (request.Status != MembershipRequestStatus.Pending)
                throw EventTransactions.Conflict("This request is already resolved.");
            request.Status = MembershipRequestStatus.Withdrawn;
            request.DecidedById = actor.Id;
            request.DecidedUtc = now;
            EventTransactions.Audit(db, eventId, actor.Id, "Membership.RequestWithdrawn", "", now);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DecideRequestAsync(Guid requestId, bool approve, string reason,
        CancellationToken cancellationToken = default)
    {
        if (!approve)
            reason = InputRules.Reason(reason);
        else
            reason = InputRules.Text(reason, "Reason", 0, 2000);
        var eventId = await RequestEventAsync(requestId, cancellationToken).ConfigureAwait(false);
        await MutateAsync(eventId, true, async (db, item, actor, now) =>
        {
            var request = await db.MembershipRequests.SingleAsync(x => x.Id == requestId, cancellationToken).ConfigureAwait(false);
            var target = approve ? MembershipRequestStatus.Approved : MembershipRequestStatus.Rejected;
            if (request.Status == target)
                return;
            EventTransactions.RequireActive(item, now);
            if (request.Status != MembershipRequestStatus.Pending)
                throw EventTransactions.Conflict("This request is already resolved.");
            if (approve)
            {
                await EventAudience.EligibleAsync(db, request.UserId, actor.TenantId, cancellationToken).ConfigureAwait(false);
                await audience.ActivateAsync(db, item, request.UserId, actor.Id, true, now,
                    reason.Length == 0 ? "Membership request approved." : reason, NotificationKind.MembershipDecided,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                request.Status = target;
                request.DecidedById = actor.Id;
                request.DecidedUtc = now;
                request.Reason = reason;
                Emit(db, eventId, actor.Id, "Membership.RequestRejected", NotificationKind.MembershipDecided,
                    [request.UserId], reason, now, [request.UserId]);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PageResult<RequestSummary>> ListRequestsAsync(Guid? eventId, PageRequest page,
        CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var manager = false;
        if (eventId is { } id)
        {
            var item = await access.RequireEventAsync(db, id, actor.Id, true, cancellationToken).ConfigureAwait(false);
            await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
            await RequireMembershipAsync(db, id, actor.Id, cancellationToken).ConfigureAwait(false);
            manager = item.Id == id;
        }
        var query = db.MembershipRequests.AsNoTracking().Where(x =>
            (eventId == null || x.EventId == eventId) && (manager || x.UserId == actor.Id));
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await (from request in query
                          join item in db.Events on request.EventId equals item.Id
                          join user in db.Users on request.UserId equals user.Id
                          orderby request.CreatedUtc descending, request.Id
                          select new { Request = request, Event = item, User = user })
            .Skip(offset).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var result = new List<RequestSummary>();
        foreach (var row in rows)
        {
            var member = await db.EventMemberships.AnyAsync(x => x.EventId == row.Event.Id &&
                x.UserId == actor.Id && x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false);
            var status = row.Request.Status == MembershipRequestStatus.Pending &&
                EventTransactions.Effective(row.Event, now) != EventStatus.Active
                    ? MembershipRequestStatus.Rejected : row.Request.Status;
            result.Add(new(row.Request.Id, row.Event.Id,
                member || EventTransactions.Effective(row.Event, now) == EventStatus.Active ? row.Event.Name : "Unavailable Event",
                new(row.User.Id, row.User.DisplayName), status,
                status != row.Request.Status ? "The Event is no longer accepting requests." : row.Request.Reason, row.Request.CreatedUtc));
        }
        return new(result, total, page.Page, page.Limit);
    }

    /// <inheritdoc />
    public async Task<PageResult<MembershipSummary>> ListMembersAsync(Guid eventId, PageRequest page,
        CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var item = await access.RequireEventAsync(db, eventId, actor.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
        await RequireMembershipAsync(db, eventId, actor.Id, cancellationToken).ConfigureAwait(false);
        var owner = await db.EventOwners.AnyAsync(x => x.EventId == eventId && x.UserId == actor.Id,
            cancellationToken).ConfigureAwait(false);
        if (!owner && await db.EventStatusHistory.AnyAsync(x => x.EventId == eventId &&
            x.Previous == EventStatus.Draft && x.Next == EventStatus.Cancelled, cancellationToken).ConfigureAwait(false))
            throw EventTransactions.Unavailable();
        var query = from member in db.EventMemberships
                    join user in db.Users on member.UserId equals user.Id
                    where member.EventId == eventId && (owner || member.Status == MembershipStatus.Active)
                    orderby user.DisplayName, user.Id
                    select new MembershipSummary(new(user.Id, user.DisplayName), member.Status,
                        db.EventOwners.Any(x => x.EventId == eventId && x.UserId == user.Id));
        return new(await query.Skip(offset).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false),
            await query.CountAsync(cancellationToken).ConfigureAwait(false), page.Page, page.Limit);
    }

    /// <inheritdoc />
    public async Task AddMemberAsync(Guid eventId, Guid directoryObjectId, bool restore,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(eventId, directoryObjectId, cancellationToken).ConfigureAwait(false);
        await MutateAsync(eventId, true, async (db, item, actor, now) =>
        {
            EventTransactions.RequireEditable(item, now);
            var user = await EventAudience.ResolveLocalAsync(db, resolved, actor.TenantId, cancellationToken).ConfigureAwait(false);
            await audience.ActivateAsync(db, item, user.Id, actor.Id, restore, now,
                "An Event owner explicitly added this individual.", NotificationKind.MembershipAdded, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveMemberAsync(Guid eventId, Guid userId, string reason, CancellationToken cancellationToken = default)
    {
        reason = InputRules.Reason(reason);
        return MutateAsync(eventId, true, (db, item, actor, now) =>
            RemoveAsync(db, item, userId, actor.Id, reason, now, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task LeaveAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        MutateAsync(eventId, null, async (db, item, actor, now) =>
        {
            var existing = await db.EventMemberships.SingleOrDefaultAsync(x => x.EventId == eventId &&
                x.UserId == actor.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                throw EventTransactions.Unavailable();
            if (existing.Status == MembershipStatus.Removed)
                return;
            // Own membership permits revocation even when unpublished historical content remains owner-only.
            await RemoveAsync(db, item, actor.Id, actor.Id, "The member chose to leave this Event.", now, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public async Task InviteAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(eventId, directoryObjectId, cancellationToken).ConfigureAwait(false);
        await MutateAsync(eventId, true, async (db, item, actor, now) =>
        {
            EventTransactions.RequireActive(item, now);
            var user = await EventAudience.ResolveLocalAsync(db, resolved, actor.TenantId, cancellationToken).ConfigureAwait(false);
            await audience.InviteAsync(db, item, user.Id, actor.Id, now, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RespondToInvitationAsync(Guid invitationId, bool accept, CancellationToken cancellationToken = default)
    {
        var eventId = await InvitationEventAsync(invitationId, cancellationToken).ConfigureAwait(false);
        await MutateAsync(eventId, null, async (db, item, actor, now) =>
        {
            var invitation = await db.EventInvitations.SingleOrDefaultAsync(x => x.Id == invitationId &&
                x.UserId == actor.Id, cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
            var target = accept ? EventInvitationStatus.Accepted : EventInvitationStatus.Declined;
            if (invitation.Status == target)
                return;
            if (invitation.Status != EventInvitationStatus.Pending || invitation.ExpiresUtc <= now)
                throw EventTransactions.Conflict("This invitation is expired or already resolved.");
            EventTransactions.RequireActive(item, now);
            if (accept)
                await audience.ActivateAsync(db, item, actor.Id, actor.Id, true, now,
                    "The invited user accepted Event membership.", NotificationKind.MembershipDecided, cancellationToken).ConfigureAwait(false);
            else
            {
                invitation.Status = target;
                invitation.ResolvedUtc = now;
                var owners = await EventAudience.OwnerIdsAsync(db, eventId, cancellationToken).ConfigureAwait(false);
                Emit(db, eventId, actor.Id, "Invitation.Declined", NotificationKind.MembershipDecided,
                    owners.Append(actor.Id).Distinct().ToArray(), "The invited user declined Event membership.", now, [actor.Id]);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        var eventId = await InvitationEventAsync(invitationId, cancellationToken).ConfigureAwait(false);
        await MutateAsync(eventId, true, async (db, item, actor, now) =>
        {
            var invitation = await db.EventInvitations.SingleAsync(x => x.Id == invitationId, cancellationToken).ConfigureAwait(false);
            if (invitation.Status == EventInvitationStatus.Revoked)
                return;
            if (invitation.Status != EventInvitationStatus.Pending)
                throw EventTransactions.Conflict("This invitation is already resolved.");
            invitation.Status = EventInvitationStatus.Revoked;
            invitation.ResolvedUtc = now;
            Emit(db, eventId, actor.Id, "Invitation.Revoked", NotificationKind.MembershipDecided,
                [invitation.UserId], "An Event owner revoked the pending invitation.", now, [invitation.UserId]);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PageResult<EventInvitationSummary>> ListInvitationsAsync(Guid? eventId, PageRequest page,
        CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        if (eventId is { } id)
        {
            var item = await access.RequireEventAsync(db, id, actor.Id, true, cancellationToken).ConfigureAwait(false);
            await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
            await RequireMembershipAsync(db, id, actor.Id, cancellationToken).ConfigureAwait(false);
        }
        var query = db.EventInvitations.AsNoTracking().Where(x =>
            eventId == null ? x.UserId == actor.Id : x.EventId == eventId);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await (from invitation in query
                          join item in db.Events on invitation.EventId equals item.Id
                          join user in db.Users on invitation.UserId equals user.Id
                          orderby invitation.CreatedUtc descending, invitation.Id
                          select new { Invitation = invitation, Event = item, User = user })
            .Skip(offset).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var result = new List<EventInvitationSummary>();
        foreach (var row in rows)
        {
            var member = await db.EventMemberships.AnyAsync(x => x.EventId == row.Event.Id && x.UserId == actor.Id &&
                x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false);
            var active = EventTransactions.Effective(row.Event, now) == EventStatus.Active;
            result.Add(new(row.Invitation.Id, row.Event.Id, member || active ? row.Event.Name : "Unavailable Event",
                new(row.User.Id, row.User.DisplayName), row.Invitation.Status == EventInvitationStatus.Pending &&
                (!active || row.Invitation.ExpiresUtc <= now) ? EventInvitationStatus.Expired : row.Invitation.Status, row.Invitation.ExpiresUtc));
        }
        return new(result, total, page.Page, page.Limit);
    }

    /// <inheritdoc />
    public async Task AddOwnerAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(eventId, directoryObjectId, cancellationToken).ConfigureAwait(false);
        await MutateAsync(eventId, true, async (db, item, actor, now) =>
        {
            if (item.Status == EventStatus.Archived)
                throw EventTransactions.Conflict("Archived Event ownership can only be changed through recovery or revocation.");
            var user = await EventAudience.ResolveLocalAsync(db, resolved, actor.TenantId, cancellationToken).ConfigureAwait(false);
            if (await db.EventOwners.AnyAsync(x => x.EventId == eventId && x.UserId == user.Id, cancellationToken).ConfigureAwait(false))
                return;
            var member = await db.EventMemberships.AnyAsync(x => x.EventId == eventId && x.UserId == user.Id &&
                x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false);
            if (!member)
            {
                EventTransactions.RequireEditable(item, now);
                await audience.ActivateAsync(db, item, user.Id, actor.Id, true, now,
                    "Individual membership granted for equal ownership.", NotificationKind.MembershipAdded, cancellationToken).ConfigureAwait(false);
            }
            var owners = await EventAudience.OwnerIdsAsync(db, eventId, cancellationToken).ConfigureAwait(false);
            db.EventOwners.Add(new EventOwner { EventId = eventId, UserId = user.Id });
            if (item.Status == EventStatus.Draft)
                EventTransactions.Audit(db, eventId, actor.Id, "Ownership.Added", $"User {user.Id:N}", now);
            else
                Emit(db, eventId, actor.Id, "Ownership.Added", NotificationKind.OwnershipChanged,
                    owners.Append(user.Id).Distinct().ToArray(), $"Equal owner added: {user.Id:N}.", now);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveOwnerAsync(Guid eventId, Guid userId, CancellationToken cancellationToken = default) =>
        MutateAsync(eventId, true, async (db, item, actor, now) =>
        {
            var owner = await db.EventOwners.SingleOrDefaultAsync(x => x.EventId == eventId && x.UserId == userId,
                cancellationToken).ConfigureAwait(false);
            if (owner is null)
                return;
            if (!await EligibleOwnerExistsAsync(db, eventId, userId, cancellationToken).ConfigureAwait(false))
                throw EventTransactions.Conflict("Keep at least one eligible owner with individual Event membership.");
            var owners = await EventAudience.OwnerIdsAsync(db, eventId, cancellationToken).ConfigureAwait(false);
            db.EventOwners.Remove(owner);
            if (item.Status == EventStatus.Draft)
                EventTransactions.Audit(db, eventId, actor.Id, "Ownership.Removed", $"User {userId:N}", now);
            else
                Emit(db, eventId, actor.Id, "Ownership.Removed", NotificationKind.OwnershipChanged,
                    owners, $"Equal owner removed: {userId:N}.", now);
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<Guid> StartBulkAsync(Guid eventId, Guid groupId, BulkMode mode,
        CancellationToken cancellationToken = default)
    {
        if (groupId == Guid.Empty || !Enum.IsDefined(mode))
            throw new DomainException(ErrorCode.Validation, "Choose a valid group and bulk action.", "Group");
        var operationId = Guid.NewGuid();
        await MutateAsync(eventId, true, async (db, item, actor, now) =>
        {
            EventTransactions.RequireActive(item, now);
            var since = now.AddHours(-1);
            if (await db.BulkOperations.CountAsync(x => x.ActorId == actor.Id && x.CreatedUtc >= since,
                cancellationToken).ConfigureAwait(false) >= options.BulkStartsPerHour)
                throw EventTransactions.Conflict("The bulk operation rate limit was reached. Try again later.");
            var operation = new BulkMembershipOperation
            {
                Id = operationId, EventId = eventId, ActorId = actor.Id, SourceGroupId = groupId,
                Mode = mode, CreatedUtc = now, Status = BulkStatus.Expanding
            };
            db.BulkOperations.Add(operation);
            EventTransactions.Audit(db, eventId, actor.Id, "Bulk.Started",
                $"Operation {operationId:N}; source group {groupId:N}; mode {mode}.", now);
            // Capture a database-ordered fence while the caller still holds the Event lock.
            // Wall-clock timestamps cannot order removals across hosts with clock skew.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ScheduledWork.Add(new ScheduledWork
            {
                Type = WorkTypes.BulkMembership, DeduplicationKey = $"event.bulk-membership.v1:{operationId:N}",
                PayloadJson = JsonSerializer.Serialize(new BulkMembershipPayload(1, operationId, Convert.ToBase64String(operation.Version))), DueUtc = now
            });
        }, cancellationToken).ConfigureAwait(false);
        return operationId;
    }

    /// <inheritdoc />
    public async Task<BulkOperationSummary> GetBulkAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var operation = await db.BulkOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId,
            cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
        var item = await access.RequireEventAsync(db, operation.EventId, actor.Id, true, cancellationToken).ConfigureAwait(false);
        await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
        await RequireMembershipAsync(db, item.Id, actor.Id, cancellationToken).ConfigureAwait(false);
        var outcomes = await db.BulkRecipients.Where(x => x.OperationId == operationId).GroupBy(x => x.Status)
            .Select(x => new { Status = x.Key, Count = x.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new(operation.Id, operation.Mode, operation.Status, outcomes.Sum(x => x.Count),
            outcomes.Where(x => x.Status == BulkRecipientStatus.Applied).Sum(x => x.Count),
            outcomes.Where(x => x.Status == BulkRecipientStatus.Skipped).Sum(x => x.Count),
            outcomes.Where(x => x.Status == BulkRecipientStatus.Failed).Sum(x => x.Count), operation.LastError);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeSearchAsync(query, cancellationToken).ConfigureAwait(false);
        var results = await directory.SearchUsersAsync(query.Trim(), cancellationToken).ConfigureAwait(false);
        if (results.Any(x => x.TenantId != actor.TenantId || x.ObjectId == Guid.Empty))
            throw new DomainException(ErrorCode.DependencyUnavailable, "The directory returned invalid tenant identities.");
        return results.Where(x => x.IsEligible).DistinctBy(x => x.ObjectId).Take(100).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default)
    {
        await AuthorizeSearchAsync(query, cancellationToken).ConfigureAwait(false);
        var results = await directory.SearchGroupsAsync(query.Trim(), cancellationToken).ConfigureAwait(false);
        if (results.Any(x => x.ObjectId == Guid.Empty))
            throw new DomainException(ErrorCode.DependencyUnavailable, "The directory returned invalid group identities.");
        return results.DistinctBy(x => x.ObjectId).Take(100).ToArray();
    }

    private async Task MutateAsync(Guid eventId, bool? ownerOnly,
        Func<ISidequestDbContext, Event, UserAccount, DateTimeOffset, Task> mutation, CancellationToken cancellationToken)
    {
        await ReconcileAsync(eventId, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var item = await EventTransactions.LockAsync(db, eventId, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        if (ownerOnly is { } requireOwner)
        {
            await access.RequireEventAsync(db, eventId, actor.Id, requireOwner, cancellationToken).ConfigureAwait(false);
            await RequireMembershipAsync(db, eventId, actor.Id, cancellationToken).ConfigureAwait(false);
        }
        else if (item.Status == EventStatus.Draft)
        {
            await access.RequireEventAsync(db, eventId, actor.Id, true, cancellationToken).ConfigureAwait(false);
            await RequireMembershipAsync(db, eventId, actor.Id, cancellationToken).ConfigureAwait(false);
        }
        await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (await EventTransactions.CompleteAsync(db, item, quests, now, cancellationToken).ConfigureAwait(false))
        {
            // The cutoff may have passed while waiting for the aggregate lock. Persist system cleanup
            // before retrying the requested command so a rejected command cannot roll completion back.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await MutateAsync(eventId, ownerOnly, mutation, cancellationToken).ConfigureAwait(false);
            return;
        }
        await mutation(db, item, actor, now).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var candidate = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == eventId,
            cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
        await RequireTenantAsync(db, candidate, actor, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var completing = EventTransactions.Effective(candidate, now) == EventStatus.Completed && candidate.Status == EventStatus.Active;
        var expired = await db.EventInvitations.AnyAsync(x => x.EventId == eventId &&
            x.Status == EventInvitationStatus.Pending && x.ExpiresUtc <= now, cancellationToken).ConfigureAwait(false);
        if (!completing && !expired)
            return;
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var item = await EventTransactions.LockAsync(db, eventId, cancellationToken).ConfigureAwait(false);
        await EventTransactions.CompleteAsync(db, item, quests, now, cancellationToken).ConfigureAwait(false);
        var invitations = await db.EventInvitations.Where(x => x.EventId == eventId &&
            x.Status == EventInvitationStatus.Pending && x.ExpiresUtc <= now).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var invitation in invitations.Where(x => x.Status == EventInvitationStatus.Pending))
        {
            invitation.Status = EventInvitationStatus.Expired;
            invitation.ResolvedUtc = now;
            EventTransactions.Audit(db, eventId, null, "Invitation.Expired", $"Invitation {invitation.Id:N} expired.", now);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<DirectoryUser> ResolveAsync(Guid eventId, Guid objectId, CancellationToken cancellationToken)
    {
        if (objectId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "Select a directory user.", "User");
        await using (var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false))
        {
            var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
            var item = await access.RequireEventAsync(db, eventId, actor.Id, true, cancellationToken).ConfigureAwait(false);
            await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
            await RequireMembershipAsync(db, eventId, actor.Id, cancellationToken).ConfigureAwait(false);
        }
        var resolved = await directory.GetUserAsync(objectId, cancellationToken).ConfigureAwait(false);
        if (resolved.ObjectId != objectId)
            throw new DomainException(ErrorCode.DependencyUnavailable, "The directory returned an unexpected identity.");
        return resolved;
    }

    private async Task RemoveAsync(ISidequestDbContext db, Event item, Guid userId, Guid actorId,
        string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.EventOwners.AnyAsync(x => x.EventId == item.Id && x.UserId == userId, cancellationToken).ConfigureAwait(false) ||
            await (from owner in db.QuestOwners
                   join quest in db.Quests on owner.QuestId equals quest.Id
                   where quest.EventId == item.Id && owner.UserId == userId
                   select owner.Id).AnyAsync(cancellationToken).ConfigureAwait(false))
            throw EventTransactions.Conflict("Remove all Event and child Quest ownership assignments before removing membership.");
        var member = await db.EventMemberships.SingleOrDefaultAsync(x => x.EventId == item.Id && x.UserId == userId,
            cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
        if (member.Status == MembershipStatus.Removed)
            return;
        member.Status = MembershipStatus.Removed;
        member.ChangedById = actorId;
        member.ChangedUtc = now;
        await quests.RemoveMemberParticipationAsync(db, item.Id, userId, actorId, reason, now, cancellationToken).ConfigureAwait(false);
        var pending = await db.EventInvitations.Where(x => x.EventId == item.Id && x.UserId == userId &&
            x.Status == EventInvitationStatus.Pending).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var invitation in pending)
        {
            invitation.Status = EventInvitationStatus.Revoked;
            invitation.ResolvedUtc = now;
        }
        var changeId = EventTransactions.Audit(db, item.Id, actorId, "Membership.Removed", $"User {userId:N}: {reason}", now);
        if (item.Status != EventStatus.Draft &&
            !await db.EventStatusHistory.AnyAsync(x => x.EventId == item.Id &&
                x.Previous == EventStatus.Draft && x.Next == EventStatus.Cancelled, cancellationToken).ConfigureAwait(false))
            changes.Append(db, new ChangeEnvelope(changeId, NotificationKind.AccessRemoved, item.Id, null,
                actorId, [userId], now, Reason: reason, AffectedUserIds: [userId]));
    }

    private async Task<Guid> RequestEventAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        return await db.MembershipRequests.Where(x => x.Id == requestId).Select(x => (Guid?)x.EventId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
    }

    private async Task<Guid> InvitationEventAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        return await db.EventInvitations.Where(x => x.Id == invitationId).Select(x => (Guid?)x.EventId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
    }

    private static Task<bool> EligibleOwnerExistsAsync(ISidequestDbContext db, Guid eventId, Guid? excluded,
        CancellationToken cancellationToken) =>
        (from owner in db.EventOwners
         join user in db.Users on owner.UserId equals user.Id
         join member in db.EventMemberships on new { owner.EventId, owner.UserId } equals new { member.EventId, member.UserId }
         where owner.EventId == eventId && owner.UserId != excluded && user.IsEligible &&
               user.DepartureVerifiedUtc == null && member.Status == MembershipStatus.Active
         select owner.Id).AnyAsync(cancellationToken);

    private static async Task RequireTenantAsync(ISidequestDbContext db, Event item, UserAccount actor, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(x => x.Id == item.CreatorId && x.TenantId == actor.TenantId, cancellationToken).ConfigureAwait(false))
            throw EventTransactions.Unavailable();
    }

    private static async Task RequireMembershipAsync(ISidequestDbContext db, Guid eventId, Guid actorId, CancellationToken cancellationToken)
    {
        if (!await db.EventMemberships.AnyAsync(x => x.EventId == eventId && x.UserId == actorId &&
            x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false))
            throw EventTransactions.Unavailable();
    }

    /// <inheritdoc />
    public async Task<int> GetCancellationImpactAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var item = await access.RequireEventAsync(db, eventId, actor.Id, true, cancellationToken).ConfigureAwait(false);
        await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
        await RequireMembershipAsync(db, eventId, actor.Id, cancellationToken).ConfigureAwait(false);
        if (EventTransactions.Effective(item, clock.GetUtcNow()) is not (EventStatus.Draft or EventStatus.Active))
            throw EventTransactions.Conflict("This Event can no longer be cancelled.");
        return await db.Quests.CountAsync(x => x.EventId == eventId &&
            (x.Status == QuestStatus.Draft || x.Status == QuestStatus.Active || x.Status == QuestStatus.Suspended),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PageResult<BulkRecipientSummary>> ListBulkRecipientsAsync(Guid operationId, PageRequest page,
        CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var operation = await db.BulkOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId,
            cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
        var item = await access.RequireEventAsync(db, operation.EventId, actor.Id, true, cancellationToken).ConfigureAwait(false);
        await RequireTenantAsync(db, item, actor, cancellationToken).ConfigureAwait(false);
        await RequireMembershipAsync(db, item.Id, actor.Id, cancellationToken).ConfigureAwait(false);
        var query = from recipient in db.BulkRecipients
                    join user in db.Users on recipient.UserId equals user.Id
                    where recipient.OperationId == operationId
                    orderby user.DisplayName, user.Id
                    select new BulkRecipientSummary(new(user.Id, user.DisplayName), recipient.Status, recipient.Detail);
        return new(await query.Skip(offset).Take(page.Limit).ToListAsync(cancellationToken).ConfigureAwait(false),
            await query.CountAsync(cancellationToken).ConfigureAwait(false), page.Page, page.Limit);
    }

    private void Emit(ISidequestDbContext db, Guid eventId, Guid actorId, string action,
        NotificationKind kind, Guid[] recipients, string reason, DateTimeOffset now, Guid[]? affectedUserIds = null)
    {
        var changeId = EventTransactions.Audit(db, eventId, actorId, action, reason, now);
        changes.Append(db, new ChangeEnvelope(changeId, kind, eventId, null, actorId, recipients.Distinct().ToArray(),
            now, Reason: reason, AffectedUserIds: affectedUserIds?.Distinct().ToArray()));
    }

    private async Task<UserAccount> AuthorizeSearchAsync(string query, CancellationToken cancellationToken)
    {
        InputRules.Text(query, "Search", 2, 100);
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var window = SearchWindows.GetOrAdd(actor.Id, _ => new SearchWindow());
        lock (window)
        {
            if (now >= window.Start.AddMinutes(1))
            {
                window.Start = now;
                window.Count = 0;
            }
            if (++window.Count > options.DirectorySearchesPerMinute)
                throw EventTransactions.Conflict("The directory search rate limit was reached. Try again shortly.");
        }
        if (SearchWindows.Count > 10000)
        {
            foreach (var pair in SearchWindows.Where(x => x.Value.Start < now.AddMinutes(-5)))
                SearchWindows.TryRemove(pair.Key, out _);
        }
        return actor;
    }

    private static EventInput Validate(EventInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = new EventInput(InputRules.Text(input.Name, "Name", 3, 120),
            InputRules.Text(input.Description, "Description", 0, 10000),
            InputRules.Text(input.DiscoverySummary, "DiscoverySummary", 0, 300),
            input.StartDate, input.EndDate, InputRules.Text(input.TimeZoneId, "TimeZoneId", 1, 100));
        TimeRules.EventWindow(result.StartDate, result.EndDate, result.TimeZoneId);
        return result;
    }

    private static void Copy(Event item, EventInput input)
    {
        item.Name = input.Name;
        item.Description = input.Description;
        item.DiscoverySummary = input.DiscoverySummary;
        item.StartDate = input.StartDate;
        item.EndDate = input.EndDate;
        item.TimeZoneId = input.TimeZoneId;
    }

    private sealed class SearchWindow
    {
        internal DateTimeOffset Start;
        internal int Count;
    }
}
