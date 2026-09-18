using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Sidequest.Web.Authentication;

/// <summary>Maps the HTTP-only authentication flows and their loopback and redirect guards.</summary>
/// <remarks>Map endpoints only during startup. Guards have no shared state; request contexts must not be shared across requests.</remarks>
public static class AuthenticationEndpoints
{
    /// <summary>Checks whether the direct peer address is a loopback address.</summary>
    /// <param name="context">The request whose connection address is inspected; forwarded headers are not consulted.</param>
    /// <returns><see langword="true"/> for a known loopback peer; otherwise <see langword="false"/>.</returns>
    public static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    /// <summary>Accepts a local absolute path or substitutes the application root for an unsafe redirect.</summary>
    /// <param name="value">The untrusted return URL from a query string or form.</param>
    /// <returns>A root-relative path without backslashes or control characters; <c>/</c> when rejected.</returns>
    public static string LocalReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] == '/' &&
        (value.Length == 1 || value[1] is not ('/' or '\\')) &&
        !value.Any(c => char.IsControl(c) || c == '\\') ? value : "/";

    /// <summary>Maps either synthetic sign-in or Entra challenge, plus antiforgery-protected sign-out.</summary>
    /// <param name="app">The application receiving authentication endpoints.</param>
    /// <param name="settings">Validated startup settings selecting the mutually exclusive authentication mode.</param>
    /// <remarks>Synthetic sign-in provisions SQL accounts before issuing a cookie. Entra provisioning occurs during token validation.</remarks>
    /// <example>
    /// <code>
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// app.UseAntiforgery();
    /// app.MapFoundationAuthentication(settings);
    /// </code>
    /// </example>
    public static void MapFoundationAuthentication(this WebApplication app, FoundationAuthenticationSettings settings)
    {
        if (settings.IsDevelopment)
        {
            app.MapPost("/auth/development", async (
                HttpContext context,
                IAntiforgery antiforgery,
                WorkforceAccounts accounts,
                DevelopmentDataSeeder seeder) =>
            {
                if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var persona = DevelopmentPersonas.All.SingleOrDefault(p => p.Name == form["persona"].ToString());
                if (persona is null) return Results.BadRequest("Choose a documented synthetic persona.");
                var principal = DevelopmentPersonas.CreatePrincipal(persona);
                await accounts.ProvisionAsync(principal, context.RequestAborted);
                await seeder.SeedAsync(context.RequestAborted);
                WorkforceSession.Stamp(principal, context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
                var properties = ExperienceAuthentication.CreateProperties(form["returnUrl"], form["experienceEpoch"]);
                await context.SignInAsync(FoundationAuthenticationSettings.CookieScheme, principal, properties);
                return Results.LocalRedirect(properties.RedirectUri!);
            });
        }
        else
        {
            app.MapGet("/auth/login", (string? returnUrl, string? experienceEpoch) => Results.Challenge(
                ExperienceAuthentication.CreateProperties(returnUrl, experienceEpoch),
                [OpenIdConnectDefaults.AuthenticationScheme]));
        }

        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var destination = form["experienceClearFailed"] == "true" ? "/?deviceClearFailed=true" : "/";
            if (settings.IsDevelopment)
                await context.SignOutAsync(FoundationAuthenticationSettings.CookieScheme);
            else
                return Results.SignOut(new AuthenticationProperties { RedirectUri = destination },
                    [FoundationAuthenticationSettings.CookieScheme, OpenIdConnectDefaults.AuthenticationScheme]);
            return Results.LocalRedirect(destination);
        });
    }
}
