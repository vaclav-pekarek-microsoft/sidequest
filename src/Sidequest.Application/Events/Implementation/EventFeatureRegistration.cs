using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;

namespace Sidequest.Application.Events.Implementation;

/// <summary>Registers real Event application workflows and their versioned job handlers without substituting providers.</summary>
public static class EventFeatureRegistration
{
    /// <summary>Registers operation-scoped Event services and both generic-dispatcher handlers.</summary>
    /// <param name="services">Host service registrations.</param>
    /// <param name="options">Explicit workflow limits, or the documented conservative defaults.</param>
    /// <returns>The same collection for host composition.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    /// <exception cref="ArgumentException">Workflow limits are invalid.</exception>
    /// <remarks>The host must also register ISidequestDbContextFactory, IResourceAccess, IDirectoryGateway,
    /// IQuestEventLifecycle, IChangeWriter, and TimeProvider. The factory's contexts implement the shared Event lock.
    /// Missing dependencies remain explicit;
    /// this method does not register no-op lifecycle, directory, or delivery implementations.</remarks>
    public static IServiceCollection AddEventWorkflows(this IServiceCollection services, EventOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new EventOperationOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddScoped<EventService>();
        services.AddScoped<IEventService>(provider => provider.GetRequiredService<EventService>());
        services.AddScoped<IEventManagementQueries>(provider => provider.GetRequiredService<EventService>());
        services.AddScoped<IEventLifecycleReconciler, EventLifecycleReconciler>();
        services.AddScoped<IBackgroundWorkHandler, EventCompletionHandler>();
        services.AddScoped<IBackgroundWorkHandler, BulkMembershipHandler>();
        return services;
    }
}
