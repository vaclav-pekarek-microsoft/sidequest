namespace Sidequest.Web.Hosting;

/// <summary>Restores the external HTTPS scheme before authentication and redirection only for the opted-in platform proxy boundary.</summary>
internal sealed class AzureAppServiceProxyStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseWhen(context => context.Connection.RemoteIpAddress is not null,
            proxy => proxy.UseForwardedHeaders());
        next(app);
    };
}
