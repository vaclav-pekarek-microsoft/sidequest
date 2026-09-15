using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sidequest.Web.Operations;

/// <summary>Composes opt-in SQL queue sampling without public diagnostics routes or external provider probes.</summary>
public static class OperationalMonitoringRegistration
{
    /// <summary>Registers disabled-state logging or a sequential scoped sampler with a container-owned Meter.</summary>
    /// <param name="services">Host services with logging and the provider-neutral operational queue reader registered.</param>
    /// <param name="configuration">Operations:Monitoring:Enabled (false by default) and :SampleInterval (default 00:00:30).</param>
    /// <returns>The supplied collection for subsequent host composition.</returns>
    /// <exception cref="ArgumentNullException">A required input is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The interval is outside five seconds through five minutes, inclusive.</exception>
    /// <exception cref="InvalidOperationException">A monitoring setting cannot be parsed.</exception>
    /// <remarks>
    /// Call once before building the host. No connection is opened at registration. Missing settings keep
    /// synthetic/unit hosts inactive. Reuses an existing TimeProvider, otherwise registers TimeProvider.System.
    /// Operational persistence ports are registered separately; readiness must remain wired even when this sampler is disabled.
    /// Parent hosting must enable deployment settings and configure an exporter for Sidequest.Operations;
    /// registering this method alone proves neither telemetry ingestion nor production release approval.
    /// Stop hosted services before disposing the container so in-flight SQL and its scope are awaited.
    /// </remarks>
    public static IServiceCollection AddSidequestOperationalMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        bool enabled;
        TimeSpan? interval;
        try
        {
            var section = configuration.GetSection("Operations:Monitoring");
            enabled = section.GetValue("Enabled", false);
            interval = section.GetValue<TimeSpan?>("SampleInterval");
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("Operational monitoring settings could not be parsed.");
        }
        var options = new OperationalMonitoringOptions(enabled, interval);
        services.AddSingleton(options);
        if (!enabled)
        {
            services.AddHostedService<DisabledOperationalMonitoringService>();
            return services;
        }
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SqlQueueSampler>();
        services.AddSingleton<OperationalQueueMetrics>();
        services.AddHostedService<OperationalMonitoringService>();
        return services;
    }
}
