using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Authentication;

public static class AuthenticationRegistration
{
    public static IServiceCollection AddFoundationAuthentication(this IServiceCollection services,
        FoundationAuthenticationSettings settings, IConfiguration configuration)
    {
        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = FoundationAuthenticationSettings.CookieScheme;
            options.DefaultChallengeScheme = FoundationAuthenticationSettings.CookieScheme;
        });
        if (settings.IsDevelopment)
            authentication.AddCookie(FoundationAuthenticationSettings.CookieScheme);
        else
        {
            authentication.AddMicrosoftIdentityWebApp(configuration.GetSection("AzureAd"));
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.SaveTokens = false;
                var previous = options.Events.OnTokenValidated;
                options.Events.OnTokenValidated = async context =>
                {
                    if (previous is not null) await previous(context);
                    if (context.Result?.Failure is not null || context.Principal is null) return;
                    try
                    {
                        await context.HttpContext.RequestServices.GetRequiredService<WorkforceAccounts>()
                            .ProvisionAsync(context.Principal, context.HttpContext.RequestAborted);
                        WorkforceSession.Stamp(context.Principal,
                            context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<WorkforceAccounts>>();
                        logger.LogWarning("Sign-in rejected ({Category}); correlation {CorrelationId}. Check workforce assignment, local eligibility and SQL availability.",
                            exception is DomainException ? "eligibility" : "provisioning", context.HttpContext.TraceIdentifier);
                        context.Fail("Sidequest eligibility or account provisioning failed.");
                    }
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/signin?failed=true");
                    return Task.CompletedTask;
                };
            });
        }

        services.PostConfigure<CookieAuthenticationOptions>(FoundationAuthenticationSettings.CookieScheme, options =>
        {
            options.Cookie.Name = settings.IsDevelopment ? "Sidequest.Synthetic" : "__Host-Sidequest";
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
