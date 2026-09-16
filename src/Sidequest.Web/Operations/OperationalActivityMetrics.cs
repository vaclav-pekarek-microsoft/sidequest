using System.Diagnostics.Metrics;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Operations;

/// <summary>Records only fixed operation/outcome dimensions; never observes arguments, results or exception text.</summary>
internal sealed class OperationalActivityMetrics : IDisposable
{
    private readonly TimeProvider clock;
    private readonly Meter meter;
    private readonly Counter<long> completed;
    private readonly Histogram<double> duration;
    private readonly Counter<long> http;

    internal OperationalActivityMetrics(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        meter = new Meter(OperationalQueueMetrics.MeterName, "1.0.0");
        completed = meter.CreateCounter<long>("sidequest.operation.completed", "{operation}",
            "Completed port invocations, not unique user commands or provider HTTP attempts.");
        duration = meter.CreateHistogram<double>("sidequest.operation.duration", "s",
            "Elapsed port invocation time, including its internal provider retries; not end-to-end command latency.");
        http = meter.CreateCounter<long>("sidequest.http.completed", "{request}",
            "HTTP request outcomes, including static assets and health probes; not interactive circuit commands.");
    }

    internal async Task<T> ObserveAsync<T>(OperationalActivity activity, Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var operation = activity switch
        {
            OperationalActivity.DirectoryUserSearch => "directory_user_search",
            OperationalActivity.DirectoryGroupSearch => "directory_group_search",
            OperationalActivity.DirectoryUserLookup => "directory_user_lookup",
            OperationalActivity.DirectoryGroupExpansion => "directory_group_expansion",
            OperationalActivity.ImageSanitization => "image_sanitization",
            OperationalActivity.EmailSubmission => "email_submission",
            OperationalActivity.UserAuthorization => "authorize_user",
            OperationalActivity.AdministratorAuthorization => "authorize_administrator",
            OperationalActivity.EventAuthorization => "authorize_event",
            OperationalActivity.QuestAuthorization => "authorize_quest",
            _ => throw new ArgumentOutOfRangeException(nameof(activity))
        };
        var start = clock.GetTimestamp();
        var outcome = "failed";
        try
        {
            var result = await action().ConfigureAwait(false);
            outcome = "succeeded";
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (DomainException error)
        {
            outcome = error.Code switch
            {
                ErrorCode.Validation => "rejected",
                ErrorCode.Forbidden => "denied",
                ErrorCode.NotFound => "unavailable",
                ErrorCode.Conflict => "conflict",
                ErrorCode.DependencyUnavailable => "dependency_failure",
                _ => "failed"
            };
            throw;
        }
        catch (DeliveryTransportException error)
        {
            outcome = error.Outcome switch
            {
                TransportOutcome.Permanent => "permanent_failure",
                TransportOutcome.Retryable => "retryable_failure",
                TransportOutcome.Uncertain => "uncertain_failure",
                _ => "failed"
            };
            throw;
        }
        finally
        {
            var operationTag = new KeyValuePair<string, object?>("operation", operation);
            var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);
            completed.Add(1, operationTag, outcomeTag);
            duration.Record(clock.GetElapsedTime(start).TotalSeconds, operationTag, outcomeTag);
        }
    }

    internal void RecordHttp(int status, bool completedNormally, bool requestAborted)
    {
        var outcome = !completedNormally ? requestAborted ? "aborted" : "server_error" : status switch
        {
            401 => "unauthenticated",
            403 => "forbidden",
            >= 500 => "server_error",
            >= 400 => "client_error",
            >= 300 => "redirect",
            >= 200 => "succeeded",
            _ => "other"
        };
        http.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    /// <inheritdoc />
    public void Dispose() => meter.Dispose();
}
