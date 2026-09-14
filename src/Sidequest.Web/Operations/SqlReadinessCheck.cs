using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Sidequest.Web.Operations;

/// <summary>Checks SQL connectivity with a read-only scalar command while exposing no connection details.</summary>
/// <param name="configuration">Supplies the <c>Sidequest</c> connection string from host configuration.</param>
public sealed class SqlReadinessCheck(IConfiguration configuration) : IHealthCheck
{
    /// <inheritdoc/>
    /// <remarks>The command timeout is five seconds. Success indicates availability, not schema or migration readiness.</remarks>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(configuration.GetConnectionString("Sidequest"));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
