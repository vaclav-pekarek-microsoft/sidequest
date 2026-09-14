using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;

namespace Sidequest.Infrastructure.Persistence;

/// <summary>Adapts EF Core's factory to the application persistence boundary.</summary>
/// <param name="factory">The configured factory that creates independent SQL Server contexts.</param>
public sealed class SidequestDbContextFactory(IDbContextFactory<SidequestDbContext> factory) : ISidequestDbContextFactory
{
    /// <inheritdoc />
    public async Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
}
