using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Events;

public enum EventListKind { Mine, Available, History }
public sealed record PersonSummary(Guid Id, string DisplayName);
public sealed record OwnerSummary(Guid Id, string DisplayName, string Email);
public sealed record EventSummary(Guid Id, string Name, string DiscoverySummary, DateOnly StartDate,
    DateOnly EndDate, string TimeZoneId, EventStatus Status, IReadOnlyList<OwnerSummary> Owners,
    bool IsMember, bool IsOwner, string Version);
public sealed record EventDetail(EventSummary Summary, string? Description);
public sealed record EventInput(string Name, string Description, string DiscoverySummary,
    DateOnly StartDate, DateOnly EndDate, string TimeZoneId);
public sealed record MembershipSummary(PersonSummary User, MembershipStatus Status, bool IsOwner);
public sealed record RequestSummary(Guid Id, Guid EventId, string EventName, PersonSummary User,
    MembershipRequestStatus Status, string Reason, DateTimeOffset CreatedUtc);
public sealed record EventInvitationSummary(Guid Id, Guid EventId, string EventName,
    PersonSummary User, EventInvitationStatus Status, DateTimeOffset ExpiresUtc);
public sealed record DuplicateEvent(EventSummary Event, double Similarity);
public sealed record BulkOperationSummary(Guid Id, BulkMode Mode, BulkStatus Status,
    int Total, int Applied, int Skipped, int Failed, string? Error);

public interface IEventService
{
    Task<PageResult<EventSummary>> ListAsync(EventListKind kind, PageRequest page, CancellationToken cancellationToken = default);
    Task<EventDetail> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(EventInput input, CancellationToken cancellationToken = default);
    Task EditAsync(Guid id, string version, EventInput input, CancellationToken cancellationToken = default);
    Task ChangeStatusAsync(Guid id, string version, EventStatus target, string reason, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DuplicateEvent>> FindDuplicatesAsync(string name, DateOnly start, DateOnly end, CancellationToken cancellationToken = default);
    Task RequestMembershipAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task WithdrawRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task DecideRequestAsync(Guid requestId, bool approve, string reason, CancellationToken cancellationToken = default);
    Task<PageResult<RequestSummary>> ListRequestsAsync(Guid? eventId, PageRequest page, CancellationToken cancellationToken = default);
    Task<PageResult<MembershipSummary>> ListMembersAsync(Guid eventId, PageRequest page, CancellationToken cancellationToken = default);
    Task AddMemberAsync(Guid eventId, Guid directoryObjectId, bool restore, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(Guid eventId, Guid userId, string reason, CancellationToken cancellationToken = default);
    Task LeaveAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task InviteAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default);
    Task RespondToInvitationAsync(Guid invitationId, bool accept, CancellationToken cancellationToken = default);
    Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default);
    Task<PageResult<EventInvitationSummary>> ListInvitationsAsync(Guid? eventId, PageRequest page, CancellationToken cancellationToken = default);
    Task AddOwnerAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default);
    Task RemoveOwnerAsync(Guid eventId, Guid userId, CancellationToken cancellationToken = default);
    Task<Guid> StartBulkAsync(Guid eventId, Guid groupId, BulkMode mode, CancellationToken cancellationToken = default);
    Task<BulkOperationSummary> GetBulkAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default);
}

public interface IQuestEventLifecycle
{
    Task CancelForEventAsync(ISidequestDbContext db, Guid eventId, Guid? actorId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task CompleteForEventAsync(ISidequestDbContext db, Guid eventId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RemoveMemberParticipationAsync(ISidequestDbContext db, Guid eventId, Guid userId, Guid actorId,
        string reason, DateTimeOffset now, CancellationToken cancellationToken = default);
}
