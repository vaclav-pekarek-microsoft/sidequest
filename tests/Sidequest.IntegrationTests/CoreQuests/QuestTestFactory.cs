using Sidequest.Application.Abstractions;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

internal sealed class QuestTestFactory(SqlTestDatabase database) : ISidequestDbContextFactory
{
    /// <inheritdoc />
    public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ISidequestDbContext>(database.CreateContext());
    }
}
