using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Quests;

public enum QuestListKind { Joined, Following, Organizing, Discover, Invited, History, Moderation }
public sealed record QuestInput(string Title, string Description, string Location, int? SuggestedCapacity,
    DateTime StartLocal, DateTime EndLocal, TimeSpan? StartOffset, TimeSpan? EndOffset, QuestVisibility Visibility);
public sealed record QuestSummary(Guid Id, Guid EventId, string EventName, string Title, string Location,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId, QuestStatus Status, QuestVisibility Visibility,
    int AttendeeCount, int FollowerCount, int? SuggestedCapacity, ParticipationStatus Participation,
    bool IsOwner, bool CanModerate, string Version, Guid? CoverAssetId);
public sealed record QuestDetail(QuestSummary Summary, string Description, string StatusReason,
    IReadOnlyList<OwnerSummary> Owners, IReadOnlyList<PersonSummary> Attendees,
    IReadOnlyList<PersonSummary>? Followers, IReadOnlyList<PersonSummary>? Invitees);
public sealed record QuestHistoryItem(string Action, string Reason, DateTimeOffset OccurredUtc, string? Actor);
public sealed record OfflineQuest(Guid Id, Guid EventId, string Title, string Location,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId, QuestStatus Status);

public interface IQuestService
{
    Task<PageResult<QuestSummary>> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page,
        CancellationToken cancellationToken = default);
    Task<QuestDetail> GetAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(Guid eventId, QuestInput input, CancellationToken cancellationToken = default);
    Task EditAsync(Guid id, string version, QuestInput input, CancellationToken cancellationToken = default);
    Task ChangeStatusAsync(Guid id, string version, QuestStatus target, string reason, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default);
    Task ParticipateAsync(Guid id, ParticipationCommand command, CancellationToken cancellationToken = default);
    Task InviteAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task RevokeInvitationAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default);
    Task RemoveAttendeeAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default);
    Task AddOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task RemoveOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuestHistoryItem>> HistoryAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OfflineQuest>> GetOfflineJoinedAsync(CancellationToken cancellationToken = default);
}
