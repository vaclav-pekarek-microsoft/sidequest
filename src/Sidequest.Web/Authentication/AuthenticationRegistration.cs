using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Sidequest.Web.Authentication;

/// <summary>Registers mode-isolated cookie authentication with fail-closed persisted account checks.</summary>
/// <remarks>Configure the service collection on the startup thread before building the host; handlers use request-scoped account services.</remarks>
public static class AuthenticationRegistration
{
    /// <summary>Configures mode-isolated cookies and request-level SQL revalidation.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="settings">Authentication settings already validated for the host environment.</param>
    /// <returns>The same service collection for further registration.</returns>
    /// <remarks>Cookies have a one-hour non-sliding lifetime and never contain a one-time code.</remarks>
    /// <example>
    /// <code>
    /// var settings = FoundationAuthenticationSettings.Load(builder.Configuration, builder.Environment);
    /// builder.Services.AddSingleton(settings);
    /// builder.Services.AddFoundationAuthentication(settings);
    /// </code>
    /// </example>
    public static IServiceCollection AddFoundationAuthentication(this IServiceCollection services,
        FoundationAuthenticationSettings settings)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = FoundationAuthenticationSettings.CookieScheme;
            options.DefaultChallengeScheme = FoundationAuthenticationSettings.CookieScheme;
        }).AddCookie(FoundationAuthenticationSettings.CookieScheme);

        services.PostConfigure<CookieAuthenticationOptions>(FoundationAuthenticationSettings.CookieScheme, options =>
        {
            options.Cookie.Name = settings.IsDevelopment ? "Sidequest.Synthetic" : "__Host-Sidequest.Magic";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = settings.IsDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.LoginPath = "/signin";
            options.AccessDeniedPath = "/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(1);
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = async context =>
            {
                try
                {
                    if (context.Principal is not null && await context.HttpContext.RequestServices
                        .GetRequiredService<WorkforceAccounts>().IsEligibleAsync(context.Principal, context.HttpContext.RequestAborted))
                        return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILogger<WorkforceAccounts>>()
                        .LogWarning("Session validation failed closed ({FailureType}); correlation {CorrelationId}. Check SQL availability.",
                            exception.GetType().Name, context.HttpContext.TraceIdentifier);
                }
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(FoundationAuthenticationSettings.CookieScheme);
            };
        });
        return services;
    }
}
