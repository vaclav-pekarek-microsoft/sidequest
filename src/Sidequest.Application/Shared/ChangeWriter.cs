using System.Text.Json;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Shared;

public sealed class ChangeWriter : IChangeWriter
{
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
