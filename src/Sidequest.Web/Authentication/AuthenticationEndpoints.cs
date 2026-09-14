using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Sidequest.Web.Authentication;

public static class AuthenticationEndpoints
{
    public static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    public static string LocalReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] == '/' &&
        (value.Length == 1 || value[1] is not ('/' or '\\')) &&
        !value.Any(c => char.IsControl(c) || c == '\\') ? value : "/";

    public static void MapFoundationAuthentication(this WebApplication app, FoundationAuthenticationSettings settings)
    {
        if (settings.IsDevelopment)
        {
            app.MapPost("/auth/development", async (HttpContext context, IAntiforgery antiforgery, WorkforceAccounts accounts) =>
            {
                if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var persona = DevelopmentPersonas.All.SingleOrDefault(p => p.Name == form["persona"].ToString());
                if (persona is null) return Results.BadRequest("Choose a documented synthetic persona.");
                var principal = DevelopmentPersonas.CreatePrincipal(persona);
                await accounts.ProvisionAsync(principal, context.RequestAborted);
                WorkforceSession.Stamp(principal, context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
                await context.SignInAsync(FoundationAuthenticationSettings.CookieScheme, principal,
                    new AuthenticationProperties { IsPersistent = false });
                return Results.LocalRedirect(LocalReturnUrl(form["returnUrl"]));
            });
        }
        else
        {
            app.MapGet("/auth/login", (string? returnUrl) => Results.Challenge(
                new AuthenticationProperties { RedirectUri = LocalReturnUrl(returnUrl) },
                [OpenIdConnectDefaults.AuthenticationScheme]));
        }

        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            if (settings.IsDevelopment)
                await context.SignOutAsync(FoundationAuthenticationSettings.CookieScheme);
            else
                return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                    [FoundationAuthenticationSettings.CookieScheme, OpenIdConnectDefaults.AuthenticationScheme]);
            return Results.LocalRedirect("/");
        });
    }
}
