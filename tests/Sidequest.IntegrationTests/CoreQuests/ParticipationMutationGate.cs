using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Domain.Model;

namespace Sidequest.IntegrationTests.CoreQuests;

internal sealed class ParticipationMutationGate : SaveChangesInterceptor
{
    internal TaskCompletionSource<int> Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReservationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal IInterceptor Commands => new ParticipationCommandProbe(this);

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<QuestParticipation>()
            .Any(x => x.State is EntityState.Added or EntityState.Modified))
        {
            Ready.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        return result;
    }

    private sealed class ParticipationCommandProbe(ParticipationMutationGate gate) : DbCommandInterceptor
    {
        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("[Participations]", StringComparison.Ordinal))
            {
                gate.Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
                if (command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal))
                    gate.ReservationStarted.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }
}
