using System.Net;

namespace Sidequest.UnitTests.CoreEvents;

internal sealed class ControlledHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    internal List<(HttpMethod Method, string Uri, string? Authorization, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
        return await respond(request, cancellationToken);
    }

    internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json) };
}
