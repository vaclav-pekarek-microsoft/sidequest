using Sidequest.Application.Experience;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Operations;

/// <summary>Registers feature-owned dashboard and per-circuit experience coordination without changing shared services.</summary>
public static class ExperienceRegistration
{
    /// <summary>Adds the application projection and mutable circuit-scoped coordinator.</summary>
    /// <param name="services">Application services, with existing Quest, Event and authorization registrations.</param>
    /// <returns>The same collection for composition.</returns>
    public static IServiceCollection AddSidequestExperience(this IServiceCollection services)
    {
        services.AddScoped<DashboardService>();
        services.AddScoped<ExperienceCoordinator>();
        services.AddDataProtection();
        services.AddSingleton<ExperienceSessionBinding>();
        return services;
    }
}
