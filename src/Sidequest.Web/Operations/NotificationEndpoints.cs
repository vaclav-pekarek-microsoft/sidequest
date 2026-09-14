using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidequest.Application.Notifications;

namespace Sidequest.Web.Operations;

/// <summary>Exposes recipient-authorized calendar recovery downloads without caching private calendar content.</summary>
/// <remarks>Register notification services before mapping these endpoints. Resource authorization remains in the application service.</remarks>
public static class NotificationEndpoints
{
    /// <summary>Maps the authenticated GET endpoint for a joined attendee's current Quest calendar invitation.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <returns>The route builder for additional endpoint conventions.</returns>
    /// <remarks>
    /// The request initializes only its own scoped Blazor authentication provider from the middleware-validated
    /// principal. Interactive circuits retain their independent authentication state. Failures propagate to
    /// the application's safe exception handler; downloads never extend the signed session deadline.
    /// </remarks>
    public static RouteHandlerBuilder MapSidequestNotifications(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/notifications/calendar/{questId:guid}", DownloadCalendarAsync)
            .RequireAuthorization();

    private static async Task<IResult> DownloadCalendarAsync(
        Guid questId,
        HttpContext context,
        [FromServices] AuthenticationStateProvider authenticationState,
        [FromServices] INotificationService notifications,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (authenticationState is not IHostEnvironmentAuthenticationStateProvider hostAuthenticationState)
        {
            throw new InvalidOperationException("Calendar downloads require a host-initialized authentication state provider.");
        }

        hostAuthenticationState.SetAuthenticationState(Task.FromResult(new AuthenticationState(context.User)));
        var calendar = await notifications.DownloadCalendarAsync(questId, cancellationToken);
        return Results.File(Encoding.UTF8.GetBytes(calendar),
            "text/calendar; charset=utf-8; method=REQUEST", "sidequest.ics", enableRangeProcessing: false);
    }
}
