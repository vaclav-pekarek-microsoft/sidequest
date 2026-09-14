using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Operations;

public sealed class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public static int Status(Exception exception) => exception switch
    {
        AntiforgeryValidationException => StatusCodes.Status400BadRequest,
        DomainException { Code: ErrorCode.Validation } => StatusCodes.Status400BadRequest,
        DomainException { Code: ErrorCode.Forbidden } => StatusCodes.Status403Forbidden,
        DomainException { Code: ErrorCode.NotFound } => StatusCodes.Status404NotFound,
        DomainException { Code: ErrorCode.Conflict } => StatusCodes.Status409Conflict,
        DomainException { Code: ErrorCode.DependencyUnavailable } => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = Status(exception);
        logger.LogWarning("Request failed with HTTP {Status} ({FailureType}); correlation {CorrelationId}.",
            status, exception.GetType().Name, context.TraceIdentifier);
        if (context.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType == "text/html") == true)
        {
            var correlationId = HtmlEncoder.Default.Encode(context.TraceIdentifier);
            await Results.Content($"""
                <!DOCTYPE html><html lang="en"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Request failed · Sidequest</title><link rel="stylesheet" href="/app.css"></head>
                <body><main><h1>The request could not be completed</h1>
                <p>Check the current state before retrying, or contact support with this correlation ID:</p>
                <p>{correlationId}</p><a href="/">Return home</a></main></body></html>
                """, "text/html", statusCode: status).ExecuteAsync(context);
            return true;
        }
        await Results.Problem(statusCode: status, title: "The request could not be completed.",
            detail: "Check the current state before retrying, or contact support with the correlation ID.",
            extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier })
            .ExecuteAsync(context);
        return true;
    }
}
