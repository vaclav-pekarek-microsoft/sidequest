using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;

namespace Sidequest.Application;

/// <summary>Registers foundation application policies without coupling them to infrastructure adapters or feature implementations.</summary>
public static class DependencyInjection
{
    /// <summary>Registers scoped database access/change-writing services and a default system clock unless one is already registered.</summary>
    /// <param name="services">Service collection to extend; infrastructure identity and persistence ports are supplied separately.</param>
    /// <returns>The same collection for further composition.</returns>
    public static IServiceCollection AddSidequestApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IResourceAccess, ResourceAccess>();
        services.AddScoped<IChangeWriter, ChangeWriter>();
        return services;
    }
}
