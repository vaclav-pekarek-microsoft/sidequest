using Sidequest.Application.Abstractions;
using Sidequest.Application.Media;
using Sidequest.Application.Media.Implementation;
using Sidequest.Infrastructure.Media;

namespace Sidequest.Web.Operations;

/// <summary>Composes real media use cases, sanitizer, private provider and the sixth durable handler without provider I/O.</summary>
public static class MediaRegistration
{
    /// <summary>Registers media dependencies; missing provider settings fail requested operations explicitly.</summary>
    /// <param name="services">Host services already containing the shared persistence, access and Quest lifecycle ports.</param>
    /// <param name="configuration">Deployment configuration; private Blob settings bind from Media:Storage.</param>
    /// <returns>The same collection for host composition.</returns>
    public static IServiceCollection AddSidequestMedia(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new PrivateMediaOptions();
        configuration.GetSection("Media:Storage").Bind(options);
        services.AddSingleton(options);
        services.AddSingleton<SkiaImageSanitizer>();
        services.AddSingleton<IImageSanitizer>(provider =>
        {
            var inner = provider.GetRequiredService<SkiaImageSanitizer>();
            return provider.GetService<OperationalActivityMetrics>() is { } metrics
                ? new ObservedImageSanitizer(inner, metrics) : inner;
        });
        services.AddSingleton<IPrivateMediaStorage, AzurePrivateMediaStorage>();
        services.AddScoped<IMediaService, MediaService>();
        services.AddScoped<IBackgroundWorkHandler, MediaCleanupHandler>();
        return services;
    }
}
