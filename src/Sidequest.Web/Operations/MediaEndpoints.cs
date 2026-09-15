using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidequest.Application.Media;

namespace Sidequest.Web.Operations;

/// <summary>Maps mediated private image delivery; Blob names, URLs and SAS values never cross this boundary.</summary>
public static class MediaEndpoints
{
    /// <summary>Maps authenticated GET /media/covers/{assetId}, with optional explicit audited moderation.</summary>
    /// <param name="endpoints">The host endpoint builder.</param>
    /// <returns>The mapped route for additional conventions.</returns>
    public static RouteHandlerBuilder MapSidequestMedia(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/media/covers/{assetId:guid}", ReadAsync).RequireAuthorization();

    private static async Task<IResult> ReadAsync(Guid assetId, HttpContext context,
        [FromServices] AuthenticationStateProvider authenticationState, [FromServices] IMediaService media,
        CancellationToken cancellationToken, [FromQuery] bool moderation = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Task.CompletedTask;
        });
        if (authenticationState is not IHostEnvironmentAuthenticationStateProvider host)
            throw new InvalidOperationException("Media requests require a host-initialized authentication state provider.");
        host.SetAuthenticationState(Task.FromResult(new AuthenticationState(context.User)));
        var image = await media.ReadAsync(assetId, moderation, cancellationToken);
        return Results.File(image.Data, image.ContentType, enableRangeProcessing: false);
    }
}
