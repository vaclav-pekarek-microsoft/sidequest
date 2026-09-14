using Sidequest.Domain.Model;

namespace Sidequest.Application.Abstractions;

public sealed record UserIdentity(Guid TenantId, Guid ObjectId, string DisplayName, string Email);

public interface ICurrentUser
{
    ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default);
}

public interface IResourceAccess
{
    Task<UserAccount> RequireUserAsync(ISidequestDbContext db, CancellationToken cancellationToken = default);
    Task<UserAccount> RequireAdministratorAsync(ISidequestDbContext db, CancellationToken cancellationToken = default);
    Task<Event> RequireEventAsync(ISidequestDbContext db, Guid eventId, Guid userId, bool ownerOnly = false, CancellationToken cancellationToken = default);
    Task<Quest> RequireQuestAsync(ISidequestDbContext db, Guid questId, Guid userId, bool ownerOnly = false, bool moderation = false, CancellationToken cancellationToken = default);
}

public sealed record DirectoryUser(Guid TenantId, Guid ObjectId, string DisplayName, string Email, bool IsEligible);
public sealed record DirectoryGroup(Guid ObjectId, string DisplayName);

public interface IDirectoryGateway
{
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectoryUser>> ExpandGroupAsync(Guid groupId, CancellationToken cancellationToken = default);
    Task<DirectoryUser> GetUserAsync(Guid objectId, CancellationToken cancellationToken = default);
}
