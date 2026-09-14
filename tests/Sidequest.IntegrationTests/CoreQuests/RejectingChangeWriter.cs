using Sidequest.Application.Abstractions;

namespace Sidequest.IntegrationTests.CoreQuests;

internal sealed class RejectingChangeWriter : IChangeWriter
{
    /// <inheritdoc />
    public void Append(ISidequestDbContext db, ChangeEnvelope change) =>
        throw new InvalidOperationException("Synthetic durable-intent staging failure.");
}
