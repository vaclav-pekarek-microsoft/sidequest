using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Verifies safe HTTP error representations and correlation identifiers for HTML and JSON clients.</summary>
/// <remarks>Response streams, service providers, and HTTP contexts are isolated per test invocation.</remarks>
public sealed class SafeErrorTests
{
    /// <summary>Verifies that unexpected failures preserve HTTP 500 while hiding exception type and private details.</summary>
    /// <param name="accept">The response media type requested by the client.</param>
    /// <returns>A task completing after the handler response is written, parsed, and checked.</returns>
    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public async Task ErrorsExposeCorrelationButNeverExceptionDetails(string accept)
    {
        await using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "correlation-123" };
        context.Request.Headers.Accept = accept;
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var handler = new SafeExceptionHandler(NullLogger<SafeExceptionHandler>.Instance);
        Assert.True(await handler.TryHandleAsync(context, new InvalidOperationException("private-connection-detail"), default));
        Assert.Equal(500, context.Response.StatusCode);
        body.Position = 0;
        var response = await new StreamReader(body).ReadToEndAsync();
        Assert.Contains("correlation-123", response);
        Assert.DoesNotContain("private-connection-detail", response);
        Assert.DoesNotContain("InvalidOperationException", response);
        if (accept == "text/html")
        {
            Assert.Contains("<h1>The request could not be completed</h1>", response);
            Assert.Contains("text/html", context.Response.ContentType);
        }
        else
        {
            using var json = JsonDocument.Parse(response);
            Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("correlation-123", json.RootElement.GetProperty("correlationId").GetString());
        }
    }
}
