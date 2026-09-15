using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Experience;
using Sidequest.Application.Quests;

namespace Sidequest.Web.Operations;

/// <summary>Maps explicit no-store HTTP refreshes whose identity comes from current cookies, never an old interactive circuit.</summary>
public static class ExperienceEndpoints
{
    /// <summary>Maps current-cookie session checks, authenticated complete Joined snapshots and the public root-scoped worker source.</summary>
    /// <param name="endpoints">The application's endpoint builder.</param>
    /// <returns>The snapshot endpoint for additional conventions.</returns>
    public static RouteHandlerBuilder MapSidequestExperience(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/experience/session", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.NoContent();
        }).RequireAuthorization().WithMetadata(new ExperienceApiMetadata());
        endpoints.MapGet("/service-worker.js", (HttpContext context, IWebHostEnvironment environment) =>
        {
            var source = environment.WebRootFileProvider.GetFileInfo("experience/service-worker.js");
            if (!source.Exists) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["Service-Worker-Allowed"] = "/";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(source.CreateReadStream(), "text/javascript", enableRangeProcessing: false);
        }).AllowAnonymous();
        return endpoints.MapGet("/experience/joined-snapshot", SnapshotAsync).RequireAuthorization()
            .WithMetadata(new ExperienceApiMetadata());
    }

    private sealed class ExperienceApiMetadata : IDisableCookieRedirectMetadata;

    private static async Task<IResult> SnapshotAsync(HttpContext context,
        [FromServices] AuthenticationStateProvider authenticationState,
        [FromServices] IQuestService quests,
        [FromServices] ISidequestDbContextFactory factory,
        [FromServices] IResourceAccess access,
        [FromServices] TimeProvider clock,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (authenticationState is not IHostEnvironmentAuthenticationStateProvider host)
            throw new InvalidOperationException("Experience requires request-scoped host authentication.");
        host.SetAuthenticationState(Task.FromResult(new AuthenticationState(context.User)));
        await using var db = await factory.CreateAsync(cancellationToken);
        var actor = await access.RequireUserAsync(db, cancellationToken);
        var joined = await quests.GetOfflineJoinedAsync(cancellationToken);
        return Results.Json(new OfflineSnapshot(actor.Id, clock.GetUtcNow(), joined));
    }
}
