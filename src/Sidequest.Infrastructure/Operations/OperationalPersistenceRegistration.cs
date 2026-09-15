using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sidequest.Application.Operations;

namespace Sidequest.Infrastructure.Operations;

/// <summary>Composes read-only SQL operational adapters without activating sampling or modifying existing persistence registrations.</summary>
public static class OperationalPersistenceRegistration
{
    /// <summary>Registers scoped readiness and queue readers behind provider-neutral Application contracts.</summary>
    /// <param name="services">Host services with the existing ISidequestDbContextFactory registration.</param>
    /// <returns>The same collection for continued host composition.</returns>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <remarks>
    /// Call for every host exposing SQL readiness, even when monitoring is disabled. This performs no I/O
    /// and starts no worker. Reuses an existing TimeProvider or registers TimeProvider.System.
    /// Parent hosting separately adds health checks, optional monitoring, and its supported exporter.
    /// </remarks>
    public static IServiceCollection AddSidequestOperationalPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IOperationalReadinessProbe, SqlOperationalReadinessProbe>();
        services.AddScoped<IOperationalQueueReader, SqlOperationalQueueReader>();
        return services;
    }
}
