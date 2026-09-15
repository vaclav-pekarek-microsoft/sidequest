using Sidequest.Application.Operations;

namespace Sidequest.Web.Operations;

internal sealed class OperationalMonitoringService(
    IServiceScopeFactory scopes,
    OperationalMonitoringOptions options,
    TimeProvider clock,
    OperationalQueueMetrics metrics,
    ILogger<OperationalMonitoringService> logger) : BackgroundService, IAsyncDisposable
{
    /// <inheritdoc/>
    public override void Dispose()
    {
        base.Dispose();
        ExecuteTask?.GetAwaiter().GetResult();
    }

    /// <summary>Cancels and awaits owned sampling before the container disposes its dependent meter.</summary>
    /// <returns>A task completing after SQL readers, contexts and the active service scope are disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        base.Dispose();
        if (ExecuteTask is not null)
            await ExecuteTask.ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Operational SQL monitoring enabled.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sampler = scope.ServiceProvider.GetRequiredService<SqlQueueSampler>();
                    var observations = await sampler.SampleAsync(stoppingToken).ConfigureAwait(false);
                    stoppingToken.ThrowIfCancellationRequested();
                    metrics.RecordSuccess(observations);
                }
                catch (OperationalObservationException exception)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    metrics.RecordFailure();
                    logger.LogWarning("Operational SQL observation unavailable: {Classification}.", OperationalFailureLogging.Classification(exception));
                }
                await Task.Delay(options.SampleInterval, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown is neither a failed observation nor a healthy empty queue.
        }
        finally
        {
            metrics.RecordFailure();
        }
    }
}
