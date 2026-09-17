using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Quests;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Directory;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components.Events;

namespace Sidequest.Web.Operations;

/// <summary>Connects real Event, Quest, directory and lifecycle implementations in the application host.</summary>
public static class CoreWorkflowRegistration
{
    /// <summary>Registers the core workflows, all feature work handlers, and validated worker startup in dependency order.</summary>
    /// <param name="services">Host services already containing foundation identity, persistence, application policies and delivery.</param>
    /// <param name="configuration">Merged deployment configuration with Events:Limits, Directory:Graph and Directory:Credentials sections.</param>
    /// <param name="authentication">Validated single-tenant authentication settings; directory tenants cannot override this boundary.</param>
    /// <returns>The same collection for subsequent host configuration.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Event workflow limits are invalid.</exception>
    /// <exception cref="InvalidOperationException">Configuration cannot be bound or a directory tenant differs from the admitted tenant.</exception>
    /// <remarks>
    /// Call once. Directory credentials and approved workforce policy remain required when directory operations execute.
    /// Registration does not contact Graph, SQL or email providers. Startup checks handler completeness before polling begins.
    /// </remarks>
    public static IServiceCollection AddSidequestCoreWorkflows(this IServiceCollection services,
        IConfiguration configuration, FoundationAuthenticationSettings authentication)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(authentication);
        var limits = configuration.GetSection("Events:Limits").Get<EventOperationOptions>() ?? new();
        var directory = configuration.GetSection("Directory:Graph").Get<GraphDirectoryOptions>() ?? new();
        var credentials = configuration.GetSection("Directory:Credentials").Get<GraphClientCredentialOptions>() ?? new();
        if ((directory.TenantId != Guid.Empty && directory.TenantId != authentication.TenantId) ||
            (credentials.TenantId != Guid.Empty && credentials.TenantId != authentication.TenantId))
            throw new InvalidOperationException("Directory tenants must match the configured authentication tenant.");
        if (authentication.IsHackathon)
            directory = new GraphDirectoryOptions(authentication.TenantId, authentication.HackathonParticipants)
            {
                MaximumRecipients = directory.MaximumRecipients,
                MaximumPages = directory.MaximumPages,
                MaximumThrottlingRetries = directory.MaximumThrottlingRetries
            };

        services.AddEventWorkflows(limits);
        services.AddEventUiRevalidation();
        services.AddSingleton(directory);
        services.AddSingleton(credentials);
        services.AddHttpClient<IGraphAccessTokenProvider, GraphClientCredentialTokenProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<GraphDirectoryGateway>(nameof(IDirectoryGateway))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IDirectoryGateway>(provider =>
        {
            var inner = provider.GetRequiredService<GraphDirectoryGateway>();
            return provider.GetService<OperationalActivityMetrics>() is { } metrics
                ? new ObservedDirectoryGateway(inner, metrics) : inner;
        });
        services.AddScoped<IQuestService, QuestService>();
        services.AddScoped<IQuestEventLifecycle, QuestEventLifecycle>();
        services.AddScoped<IBackgroundWorkHandler, QuestCompletionHandler>();
        services.AddHostedService<WorkHandlerStartupCheck>();
        services.AddHostedService<DurableWorkHostedService>();
        return services;
    }
}
