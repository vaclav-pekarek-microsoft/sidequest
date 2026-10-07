using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

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

    /// <summary>Checks whether a development request is connected entirely through loopback addresses.</summary>
    /// <param name="context">The request whose direct peer and local listener addresses are inspected.</param>
    /// <param name="environment">The host environment that must be Development.</param>
    /// <returns><see langword="true"/> only for a Development host with known loopback peer and listener addresses.</returns>
    public static bool IsLocalDevelopment(HttpContext context, IHostEnvironment environment) =>
        environment.IsDevelopment() &&
        IsLoopback(context) &&
        context.Connection.LocalIpAddress is { } localAddress &&
        IPAddress.IsLoopback(localAddress);

    /// <summary>Accepts a local absolute path or substitutes the application root for an unsafe redirect.</summary>
    /// <param name="value">The untrusted return URL from a query string or form.</param>
    /// <returns>A root-relative path without backslashes or control characters; <c>/</c> when rejected.</returns>
    public static string LocalReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] == '/' &&
        (value.Length == 1 || value[1] is not ('/' or '\\')) &&
        !value.Any(c => char.IsControl(c) || c == '\\') ? value : "/";

    /// <summary>Maps synthetic or Microsoft-alias magic-code sign-in plus antiforgery-protected sign-out.</summary>
    /// <param name="app">The application receiving authentication endpoints.</param>
    /// <param name="settings">Validated startup settings selecting the mutually exclusive authentication mode.</param>
    /// <remarks>Synthetic sign-in provisions SQL accounts before issuing a cookie. Magic-code verification links or creates the account transactionally.</remarks>
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
            app.MapPost("/auth/magic/request", async (
                HttpContext context,
                IAntiforgery antiforgery,
                IHostEnvironment environment,
                MagicCodeAuthenticationService magicCodes) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                Guid challenge;
                try
                {
                    challenge = await magicCodes.RequestAsync(
                        form["alias"], IsLocalDevelopment(context, environment), context.RequestAborted);
                }
                catch (ArgumentException)
                {
                    var invalidExperienceEpoch = Uri.EscapeDataString(form["experienceEpoch"].ToString());
                    return Results.LocalRedirect(
                        $"/signin?invalidAlias=true&returnUrl={Uri.EscapeDataString(LocalReturnUrl(form["returnUrl"]))}&experienceEpoch={invalidExperienceEpoch}");
                }
                var returnUrl = Uri.EscapeDataString(LocalReturnUrl(form["returnUrl"]));
                var experienceEpoch = Uri.EscapeDataString(form["experienceEpoch"].ToString());
                return Results.LocalRedirect(
                    $"/signin?sent=true&challenge={challenge:D}&returnUrl={returnUrl}&experienceEpoch={experienceEpoch}");
            });

            app.MapPost("/auth/magic/verify", async (
                HttpContext context,
                IAntiforgery antiforgery,
                MagicCodeAuthenticationService magicCodes) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                _ = Guid.TryParse(form["challenge"], out var challenge);
                var principal = await magicCodes.VerifyAsync(challenge, form["code"], context.RequestAborted);
                if (principal is null)
                {
                    var returnUrl = Uri.EscapeDataString(LocalReturnUrl(form["returnUrl"]));
                    var experienceEpoch = Uri.EscapeDataString(form["experienceEpoch"].ToString());
                    return Results.LocalRedirect(
                        $"/signin?sent=true&invalidCode=true&challenge={challenge:D}&returnUrl={returnUrl}&experienceEpoch={experienceEpoch}");
                }
                WorkforceSession.Stamp(principal,
                    context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
                var properties = ExperienceAuthentication.CreateProperties(form["returnUrl"], form["experienceEpoch"]);
                await context.SignInAsync(FoundationAuthenticationSettings.CookieScheme, principal, properties);
                return Results.LocalRedirect(properties.RedirectUri!);
            });
        }

        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var destination = form["experienceClearFailed"] == "true" ? "/?deviceClearFailed=true" : "/";
            await context.SignOutAsync(FoundationAuthenticationSettings.CookieScheme);
            return Results.LocalRedirect(destination);
        });
    }
}
