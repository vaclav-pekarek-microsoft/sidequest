using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Sidequest.Web.Components.Events;

/// <summary>Registers Event-specific circuit revalidation without altering global render mode, routing, or authentication.</summary>
public static class EventUiRegistration
{
    /// <summary>Shares a scoped reconnect notifier between the circuit handler and Event components.</summary>
    /// <param name="services">Web host registrations; call alongside the application's Event workflow registration.</param>
    /// <returns>The supplied collection for further host composition.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    public static IServiceCollection AddEventUiRevalidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<EventCircuitRevalidation>();
        services.AddScoped<CircuitHandler>(provider => provider.GetRequiredService<EventCircuitRevalidation>());
        return services;
    }
}
