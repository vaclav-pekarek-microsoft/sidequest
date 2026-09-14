using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sidequest.Infrastructure.Persistence;

/// <summary>Provides an explicitly configured SQL context to EF migration tooling without starting the web host.</summary>
/// <remarks>
/// Reads <c>SIDEQUEST_SQL_CONNECTION</c>, falling back to the named local development
/// database only. Operators must verify the target connection before applying migrations.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SidequestDbContext>
{
    /// <summary>Constructs a design-time context; it does not open, create, or migrate the database.</summary>
    /// <param name="args">Tooling arguments, unused because the connection is supplied through the environment.</param>
    /// <returns>A new context that the EF tooling caller owns and disposes.</returns>
    public SidequestDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SIDEQUEST_SQL_CONNECTION")
            ?? @"Server=(localdb)\MSSQLLocalDB;Database=SidequestDevelopment;Integrated Security=true;TrustServerCertificate=true";
        return new SidequestDbContext(new DbContextOptionsBuilder<SidequestDbContext>().UseSqlServer(connection).Options);
    }
}
