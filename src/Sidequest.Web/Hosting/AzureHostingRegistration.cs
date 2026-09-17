using Azure.Core.Cryptography;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;

namespace Sidequest.Web.Hosting;

/// <summary>Composes durable encrypted key storage and authenticated metrics for explicitly enabled Azure deployments.</summary>
public static class AzureHostingRegistration
{
    internal const string DataProtectionClientKey = "Sidequest.DataProtection";
    internal const string MetricsOptionsName = "Sidequest.AzureMetrics";
    internal const string StatsbeatDisabledVariable = "APPLICATIONINSIGHTS_STATSBEAT_DISABLED";
    internal const string SdkStatsDisabledVariable = "APPLICATIONINSIGHTS_SDKSTATS_DISABLED";

    /// <summary>Registers Azure key-ring persistence/wrapping and metrics-only export without contacting cloud services during registration.</summary>
    /// <param name="services">The application container; call once before building the host.</param>
    /// <param name="configuration">Explicit Azure hosting settings and telemetry resource routing.</param>
    /// <param name="environment">The deployment environment, which must not be synthetic Development when enabled.</param>
    /// <returns>The same service collection, unchanged when Azure hosting is disabled.</returns>
    /// <remarks>Uses only the App Service system-assigned managed identity. No developer credential chain,
    /// SAS, instrumentation-key authentication, automatic trace/log export or local telemetry spool is enabled.
    /// The process must explicitly disable Statsbeat and customer SDK statistics before initialization.
    /// Cloud permissions, private connectivity, key versions and ingestion require separate deployment verification.</remarks>
    /// <exception cref="ArgumentNullException">The container, configuration or environment is null.</exception>
    /// <exception cref="InvalidOperationException">Enabled configuration is invalid or this Azure hosting composition is registered twice.</exception>
    public static IServiceCollection AddSidequestAzureHosting(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment) =>
        AddSidequestAzureHosting(services, configuration, environment, Environment.GetEnvironmentVariable);

    internal static IServiceCollection AddSidequestAzureHosting(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment, Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(services);
        var settings = AzureHostingSettings.Load(configuration, environment);
        if (settings is null)
            return services;
        foreach (var variable in new[] { StatsbeatDisabledVariable, SdkStatsDisabledVariable })
        {
            if (!string.Equals(readEnvironment(variable), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Azure hosting requires the process environment variable {variable}=true.");
        }
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AzureHostingSettings)))
            throw new InvalidOperationException("Azure hosting must be registered only once.");

        services.AddSingleton(settings);
        if (settings.AppServiceProxyEnabled)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
            });
            services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, AzureAppServiceProxyStartupFilter>());
        }
        var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        services.TryAddKeyedSingleton<BlobClient>(DataProtectionClientKey, (_, _) => new(settings.BlobUri, credential));
        services.TryAddKeyedSingleton<IKeyEncryptionKeyResolver>(DataProtectionClientKey, (_, _) => new KeyResolver(credential));
        services.AddDataProtection()
            .SetApplicationName(settings.ApplicationName)
            .PersistKeysToAzureBlobStorage(provider => provider.GetRequiredKeyedService<BlobClient>(DataProtectionClientKey))
            .ProtectKeysWithAzureKeyVault(settings.KeyUri.AbsoluteUri,
                provider => provider.GetRequiredKeyedService<IKeyEncryptionKeyResolver>(DataProtectionClientKey));

        services.AddOpenTelemetry().WithMetrics(metrics => AddOperationalMetricSources(metrics)
            .AddAzureMonitorMetricExporter(options =>
            {
                options.ConnectionString = settings.TelemetryConnectionString;
                options.Credential = credential;
                options.DisableOfflineStorage = true;
                options.EnableLiveMetrics = false;
                options.EnableStandardMetrics = false;
                options.EnablePerformanceCounters = false;
            }, name: MetricsOptionsName));
        return services;
    }

    internal static MeterProviderBuilder AddOperationalMetricSources(MeterProviderBuilder metrics) => metrics
        .AddMeter("Sidequest.Operations")
        .AddView(instrument => instrument.Name switch
        {
            "sidequest.queue.pending" or "sidequest.queue.due" or "sidequest.queue.dead_letter" or
            "sidequest.queue.oldest_due_age" or "sidequest.queue.observation_available" or
            "sidequest.queue.observation_stale" or "sidequest.queue.observation_age" =>
                new MetricStreamConfiguration { TagKeys = ["queue"] },
            "sidequest.operation.completed" or "sidequest.operation.duration" =>
                new MetricStreamConfiguration { TagKeys = ["operation", "outcome"] },
            "sidequest.http.completed" => new MetricStreamConfiguration { TagKeys = ["outcome"] },
            _ => MetricStreamConfiguration.Drop
        });
}
