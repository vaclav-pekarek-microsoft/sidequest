using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;

namespace Sidequest.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddSidequestApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IResourceAccess, ResourceAccess>();
        services.AddScoped<IChangeWriter, ChangeWriter>();
        return services;
    }
}
