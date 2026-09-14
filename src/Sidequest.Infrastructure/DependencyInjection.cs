using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSidequestInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Sidequest")
            ?? throw new InvalidOperationException("ConnectionStrings:Sidequest must be configured.");
        services.AddDbContextFactory<SidequestDbContext>(options => options.UseSqlServer(connection));
        services.AddScoped<ISidequestDbContextFactory, SidequestDbContextFactory>();
        return services;
    }
}
