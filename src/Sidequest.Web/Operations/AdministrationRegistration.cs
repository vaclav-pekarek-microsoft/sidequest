using Sidequest.Application.Administration;

namespace Sidequest.Web.Operations;

/// <summary>Composes narrow administration operations without granting resource access, enabling external recovery, or starting providers.</summary>
public static class AdministrationRegistration
{
    /// <summary>Registers audited administration and business-email services; the recovery gate defaults closed.</summary>
    /// <param name="services">Host services already providing persistence, current-user access, outbox writer and TimeProvider.</param>
    /// <param name="configuration">Deployment configuration; only Administration:DepartureRecovery is bound here.</param>
    /// <returns>The same collection for host composition.</returns>
    /// <remarks>Operators must separately approve and secure the departure verification procedure before setting
    /// Enabled and ProcedureReference. These values are never business settings or evidence of approval.</remarks>
    public static IServiceCollection AddSidequestAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var policy = new DepartureRecoveryPolicy();
        configuration.GetSection("Administration:DepartureRecovery").Bind(policy);
        services.AddSingleton(policy);
        services.AddScoped<AdministrationService>();
        services.AddScoped<BusinessEmailService>();
        return services;
    }
}
