using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure.Persistence;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

internal sealed class ObservedContextFactory(SqlTestDatabase database, params IInterceptor[] observers) : ISidequestDbContextFactory
{
    /// <inheritdoc/>
    public async Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var template = database.CreateContext();
        var connectionString = template.Database.GetConnectionString() ??
            throw new InvalidOperationException("The owned SQL fixture is not initialized.");
        return new SidequestDbContext(new DbContextOptionsBuilder<SidequestDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(observers).Options);
    }
}
