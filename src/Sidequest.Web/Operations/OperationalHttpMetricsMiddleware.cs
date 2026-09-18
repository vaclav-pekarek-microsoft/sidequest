namespace Sidequest.Web.Operations;

internal sealed class OperationalHttpMetricsMiddleware(RequestDelegate next, OperationalActivityMetrics? metrics = null)
{
    /// <summary>Counts the final response after inner exception handling, preserving failures and request cancellation.</summary>
    /// <param name="context">The request; only its numeric status and cancellation state are inspected.</param>
    /// <returns>The downstream request completion without replay or error suppression.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var completed = false;
        try
        {
            await next(context);
            completed = true;
        }
        finally
        {
            metrics?.RecordHttp(context.Response.StatusCode, completed, context.RequestAborted.IsCancellationRequested);
        }
    }
}
