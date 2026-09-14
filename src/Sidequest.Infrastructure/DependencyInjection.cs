using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.Infrastructure;

/// <summary>Composes concrete SQL persistence behind the application-owned abstractions.</summary>
public static class DependencyInjection
{
    /// <summary>Registers per-operation SQL context creation without connecting to or migrating the database.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">Configuration containing <c>ConnectionStrings:Sidequest</c>.</param>
    /// <returns>The same service collection for further composition.</returns>
    /// <exception cref="InvalidOperationException">The required Sidequest connection string is missing.</exception>
    public static IServiceCollection AddSidequestInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Sidequest")
            ?? throw new InvalidOperationException("ConnectionStrings:Sidequest must be configured.");
        services.AddDbContextFactory<SidequestDbContext>(options => options.UseSqlServer(connection));
        services.AddScoped<ISidequestDbContextFactory, SidequestDbContextFactory>();
        return services;
    }
}
