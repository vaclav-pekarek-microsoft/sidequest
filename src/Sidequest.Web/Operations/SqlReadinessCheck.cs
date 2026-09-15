using Microsoft.Extensions.Diagnostics.HealthChecks;
using Sidequest.Application.Operations;

namespace Sidequest.Web.Operations;

/// <summary>Checks SQL availability and applied migration history without changing the database or exposing diagnostics.</summary>
/// <param name="probe">Verifies read-only persistence readiness behind the provider-neutral application contract.</param>
/// <param name="logger">Receives only fixed failure classifications, never provider exceptions.</param>
/// <exception cref="ArgumentNullException">A required dependency is null.</exception>
/// <remarks>
/// The anonymous health response remains status-only. Readiness requires a nonempty compiled migration
/// set matching applied history; this is not a full schema-drift audit or a probe of optional Graph/email.
/// The host must register <see cref="IOperationalReadinessProbe"/> even when optional queue monitoring is disabled.
/// No startup migration is performed.
/// </remarks>
public sealed class SqlReadinessCheck(IOperationalReadinessProbe probe, ILogger<SqlReadinessCheck> logger) : IHealthCheck
{
    private readonly IOperationalReadinessProbe probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly ILogger<SqlReadinessCheck> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    /// <remarks>SQL commands time out after five seconds. Cooperative cancellation is propagated, not reported as a failed probe.</remarks>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return HealthCheckResult.Healthy();
        }
        catch (OperationalObservationException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning("SQL readiness unavailable: {Classification}.", OperationalFailureLogging.Classification(exception));
            return HealthCheckResult.Unhealthy();
        }
    }
}
