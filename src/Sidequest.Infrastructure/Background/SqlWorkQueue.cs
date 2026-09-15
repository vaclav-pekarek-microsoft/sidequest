using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.Infrastructure.Background;

/// <summary>SQL Server atomic leased queue over frozen M1 tables; all updates fence stale ownership and expiry.</summary>
/// <param name="factory">Fresh context factory.</param>
/// <param name="clock">Deterministic UTC clock.</param>
/// <param name="options">Bounded lease configuration.</param>
public sealed class SqlWorkQueue(ISidequestDbContextFactory factory, TimeProvider clock, DurableWorkOptions options)
{
    /// <summary>Claims the earliest due row, including abandoned expired processing work; concurrent claimers skip locked rows.</summary>
    /// <param name="category">outbox, scheduled, or delivery.</param>
    /// <param name="cancellationToken">Cancels SQL work.</param>
    /// <returns>Ownership proof or null when no due work is available.</returns>
    public async Task<WorkLease?> ClaimAsync(string category, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ClaimCoreAsync(category, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    private async Task<WorkLease?> ClaimCoreAsync(string category, CancellationToken cancellationToken)
    {
        options.Validate();
        var table = Table(category);
        await using var context = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var db = RequireSql(context);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"""
            UPDATE [{table}] WITH (ROWLOCK) SET [Status] = 3, [LeaseId] = NULL, [LeaseUntilUtc] = NULL,
                [LastError] = 'Eight interrupted attempts exhausted; investigate before replay.'
            WHERE [Attempts] >= 8 AND (([Status] = 1 AND [LeaseUntilUtc] <= @now) OR [Status] = 0);
            ;WITH candidate AS (
                SELECT TOP (1) * FROM [{table}] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE ([Status] = 0 AND [DueUtc] <= @now)
                   OR ([Status] = 1 AND [LeaseUntilUtc] <= @now)
                ORDER BY [DueUtc], [Id]
            )
            UPDATE candidate SET [Status] = 1, [LeaseId] = @token,
                [LeaseUntilUtc] = @until, [Attempts] = [Attempts] + 1
            OUTPUT inserted.[Id], {(category == "delivery" ? "'delivery'" : "inserted.[Type]")}, inserted.[Attempts];
            """;
        var now = clock.GetUtcNow();
        var token = Guid.NewGuid();
        command.Parameters.Add(new SqlParameter("@now", now));
        command.Parameters.Add(new SqlParameter("@until", now + options.LeaseDuration));
        command.Parameters.Add(new SqlParameter("@token", token));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new(reader.GetGuid(0), category, reader.GetString(1), token, reader.GetInt32(2));
    }

    /// <summary>Renews only a still-owned, unexpired claim; never resurrects a lost lease.</summary>
    /// <param name="lease">Original claim proof.</param>
    /// <param name="cancellationToken">Cancels SQL.</param>
    /// <returns>True if renewal retained ownership.</returns>
    public Task<bool> RenewAsync(WorkLease lease, CancellationToken cancellationToken = default) =>
        UpdateAsync(lease, "[LeaseUntilUtc] = @until", null, cancellationToken);

    /// <summary>Marks successful handling only while ownership is current.</summary>
    /// <param name="lease">Original claim proof.</param>
    /// <param name="cancellationToken">Cancels SQL.</param>
    /// <returns>False if superseded, expired or reclaimed.</returns>
    public Task<bool> CompleteAsync(WorkLease lease, CancellationToken cancellationToken = default) =>
        UpdateAsync(lease, "[Status] = 2, [LeaseId] = NULL, [LeaseUntilUtc] = NULL, [LastError] = NULL", null, cancellationToken);

    /// <summary>Persists safe bounded retry or immediate permanent dead-letter status, retaining the logical key and payload.</summary>
    /// <param name="lease">Original claim proof.</param>
    /// <param name="failure">Classified failure; raw messages are not persisted.</param>
    /// <param name="cancellationToken">Cancels SQL.</param>
    /// <returns>Whether a still-current lease was updated.</returns>
    public Task<bool> FailAsync(WorkLease lease, Exception failure, CancellationToken cancellationToken = default)
    {
        var permanent = failure is DeliveryTransportException { Outcome: TransportOutcome.Permanent } or
            DomainException { Code: ErrorCode.Validation } or
            DomainException { Code: ErrorCode.DependencyUnavailable, IsPermanentDependencyFailure: true };
        var dead = permanent || lease.Attempts >= 8;
        var delay = RetryDelay(lease.Attempts, lease.Id);
        if (failure is DeliveryTransportException { RetryAfter: { } retryAfter } && retryAfter > delay)
        {
            // Do not retry earlier than an excessive provider delay; require an operator instead of unbounded automatic scheduling.
            dead |= retryAfter > TimeSpan.FromHours(24);
            delay = retryAfter > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : retryAfter;
        }
        var error = permanent ? "Permanent dependency/configuration or payload failure; correct before replay." : failure switch
        {
            DeliveryTransportException { Outcome: TransportOutcome.Uncertain } or OperationCanceledException => "Submission outcome uncertain; duplicates are possible. Calendar withdrawal obligation retained.",
            _ => "Retryable processing failure; inspect correlation-safe operational diagnostics."
        };
        return UpdateAsync(lease,
            $"[Status] = {(dead ? 3 : 0)}, [LeaseId] = NULL, [LeaseUntilUtc] = NULL, [DueUtc] = @due, [LastError] = @error",
            (clock.GetUtcNow() + delay, error), cancellationToken);
    }

    /// <summary>Calculates capped exponential backoff with stable per-work jitter, suitable for deterministic tests.</summary>
    /// <param name="attempt">One-based failed attempt.</param>
    /// <param name="workId">Stable identity supplying non-secret deterministic jitter.</param>
    /// <returns>Delay between five seconds and sixteen minutes.</returns>
    public static TimeSpan RetryDelay(int attempt, Guid workId)
    {
        var jitter = workId.ToByteArray()[0] % 11;
        return TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 8))) + jitter);
    }

    private async Task<bool> UpdateAsync(WorkLease lease, string assignments,
        (DateTimeOffset Due, string Error)? failure, CancellationToken cancellationToken)
    {
        try
        {
            return await UpdateCoreAsync(lease, assignments, failure, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    private async Task<bool> UpdateCoreAsync(WorkLease lease, string assignments,
        (DateTimeOffset Due, string Error)? failure, CancellationToken cancellationToken)
    {
        var table = Table(lease.Category);
        await using var context = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var db = RequireSql(context);
        var now = clock.GetUtcNow();
        object[] parameters =
        [
            new SqlParameter("@id", lease.Id), new SqlParameter("@token", lease.Token),
            new SqlParameter("@now", now), new SqlParameter("@until", now + options.LeaseDuration),
            new SqlParameter("@due", failure?.Due ?? now),
            new SqlParameter("@error", failure?.Error ?? "")
        ];
        var sql = $"UPDATE [{table}] SET {assignments} WHERE [Id] = @id AND [Status] = 1 AND [LeaseId] = @token AND [LeaseUntilUtc] > @now";
        return await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken).ConfigureAwait(false) == 1;
    }

    private static string Table(string category) => category switch
    {
        "outbox" => "OutboxMessages",
        "scheduled" => "ScheduledWork",
        "delivery" => "NotificationDeliveries",
        _ => throw new ArgumentException("Unsupported durable-work category.", nameof(category))
    };

    private static DbContext RequireSql(ISidequestDbContext context) =>
        context is DbContext db && db.Database.IsSqlServer() ? db :
            throw new InvalidOperationException("Durable queue requires the configured SQL Server persistence provider.");
}
