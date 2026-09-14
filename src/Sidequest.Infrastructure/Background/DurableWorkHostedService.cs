using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sidequest.Infrastructure.Background;

/// <summary>In-host durable SQL worker with fresh dependency scopes; unavailable SQL is visible but does not block synthetic web startup.</summary>
/// <param name="scopes">Creates a scope for each bounded polling pass.</param>
/// <param name="options">Healthy polling and lease bounds.</param>
/// <param name="clock">Delay seam.</param>
/// <param name="logger">Redacted worker availability diagnostics.</param>
public sealed class DurableWorkHostedService(IServiceScopeFactory scopes, DurableWorkOptions options,
    TimeProvider clock, ILogger<DurableWorkHostedService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(_ => RunLoopAsync(stoppingToken))).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<DurableWorkRunner>();
                foreach (var category in new[] { "outbox", "scheduled", "delivery" })
                    worked |= await runner.RunOnceAsync(category, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError("Durable work polling is unavailable ({FailureType}). Pending work remains in SQL.", error.GetType().Name);
            }
            if (!worked)
            {
                try { await Task.Delay(options.PollInterval, clock, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
