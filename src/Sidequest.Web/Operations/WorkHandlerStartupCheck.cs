using Sidequest.Application.Abstractions;

namespace Sidequest.Web.Operations;

/// <summary>Fails host startup before polling if versioned work does not have exactly one registered handler.</summary>
/// <param name="scopes">Creates an isolated scope to resolve handlers without invoking them or opening a database connection.</param>
/// <param name="requirements">Explicit optional feature requirements; null retains the five-handler core-only host contract.</param>
public sealed class WorkHandlerStartupCheck(IServiceScopeFactory scopes, WorkHandlerRequirements? requirements = null) : IHostedService
{
    /// <summary>Checks the complete supported work-type set and rejects missing, duplicate or unknown registrations.</summary>
    /// <param name="cancellationToken">Requests cancellation before startup validation.</param>
    /// <returns>A completed task after the handler graph is verified.</returns>
    /// <exception cref="InvalidOperationException">Handler registrations are incomplete, ambiguous or unsupported.</exception>
    /// <exception cref="OperationCanceledException">Startup was cancelled.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopes.CreateScope();
        var types = scope.ServiceProvider.GetServices<IBackgroundWorkHandler>().Select(handler => handler.WorkType).ToArray();
        List<string> expected =
        [
            WorkTypes.Change, WorkTypes.EventCompletion, WorkTypes.QuestCompletion,
            WorkTypes.BulkMembership, WorkTypes.Reminder
        ];
        if (requirements?.RequireMediaCleanup == true)
            expected.Add(WorkTypes.MediaCleanup);
        if (types.Length != expected.Count || expected.Any(type => types.Count(actual => actual == type) != 1))
            throw new InvalidOperationException("Every supported durable work type must have exactly one registered handler.");
        return Task.CompletedTask;
    }

    /// <summary>Completes shutdown without owning background tasks or provider resources.</summary>
    /// <param name="cancellationToken">Host shutdown signal; no asynchronous cleanup is required.</param>
    /// <returns>A completed shutdown task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
