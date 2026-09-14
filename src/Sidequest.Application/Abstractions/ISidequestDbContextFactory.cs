namespace Sidequest.Application.Abstractions;

/// <summary>Creates isolated operation-scoped database contexts instead of sharing mutable EF state across requests or circuits.</summary>
public interface ISidequestDbContextFactory
{
    /// <summary>Creates a new context whose lifetime and explicit transactions belong to the caller.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of context creation.</param>
    /// <returns>A new context to asynchronously dispose after the operation; creation does not imply an active explicit transaction.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default);
}
