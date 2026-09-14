using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sidequest.Application.Abstractions;

namespace Sidequest.Infrastructure.Persistence;

public sealed class SidequestDbContextFactory(IDbContextFactory<SidequestDbContext> factory) : ISidequestDbContextFactory
{
    public async Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        await factory.CreateDbContextAsync(cancellationToken);
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SidequestDbContext>
{
    public SidequestDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SIDEQUEST_SQL_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=SidequestDevelopment;Integrated Security=true;TrustServerCertificate=true";
        return new SidequestDbContext(new DbContextOptionsBuilder<SidequestDbContext>().UseSqlServer(connection).Options);
    }
}
