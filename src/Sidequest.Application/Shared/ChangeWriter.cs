using System.Text.Json;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Shared;

/// <summary>Serializes version-1 change envelopes into tracked outbox records without saving, committing, or dispatching.</summary>
public sealed class ChangeWriter : IChangeWriter
{
    /// <inheritdoc/>
    public void Append(ISidequestDbContext db, ChangeEnvelope change)
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = change.ChangeId,
            Type = WorkTypes.Change,
            AggregateId = change.QuestId ?? change.EventId,
            PayloadJson = JsonSerializer.Serialize(change),
            CorrelationId = change.ChangeId.ToString("N"),
            OccurredUtc = change.OccurredUtc,
            DueUtc = change.OccurredUtc
        });
    }
}
