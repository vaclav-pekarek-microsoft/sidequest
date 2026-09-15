namespace Sidequest.Web.Operations;

internal sealed class DisabledOperationalMonitoringService(ILogger<DisabledOperationalMonitoringService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Operational SQL monitoring disabled; no queue observations will be collected.");
        return Task.CompletedTask;
    }
}
