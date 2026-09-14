namespace Sidequest.Application.Abstractions;

/// <summary>Stages durable change delivery in the same caller-owned unit of work as domain and audit changes.</summary>
/// <remarks>The supplied context and captured recipient arrays must not be modified concurrently during serialization/staging.
/// An implementation's statelessness does not make EF tracked state safe for parallel use.</remarks>
public interface IChangeWriter
{
    /// <summary>Adds an outbox record to the supplied context without saving, committing, or calling external providers.</summary>
    /// <param name="db">Per-operation context whose explicit transaction is owned, saved, committed, and disposed by the caller.</param>
    /// <param name="change">Change to serialize; its stable identifier also identifies the outbox record.</param>
    /// <example>
    /// <code>
    /// await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
    /// await using var transaction = await db.BeginTransactionAsync(
    ///     cancellationToken: cancellationToken).ConfigureAwait(false);
    /// // Apply the authorized domain and audit changes to db before staging their envelope.
    /// changeWriter.Append(db, change);
    /// await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    /// await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    /// // Provider dispatch happens later, outside this SQL transaction.
    /// </code>
    /// </example>
    public void Append(ISidequestDbContext db, ChangeEnvelope change);
}
