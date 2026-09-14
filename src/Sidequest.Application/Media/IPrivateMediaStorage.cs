namespace Sidequest.Application.Media;

/// <summary>Stores validated image bytes in a private container without returning public URLs or authorization grants.</summary>
/// <remarks>The application authorizes access before invoking this infrastructure port.
/// Provider operations must not run inside the application's database transaction.</remarks>
public interface IPrivateMediaStorage
{
    /// <summary>Writes a new application-generated asset key without overwriting an existing object.</summary>
    /// <param name="blobName">Opaque application-generated private key, never a user-supplied path or remote URL.</param>
    /// <param name="data">Sanitized encoded image bytes.</param>
    /// <param name="contentType">Validated MIME type matching the encoded image.</param>
    /// <param name="cancellationToken">Cancels the private provider operation.</param>
    /// <returns>A task completing only after the provider confirms the write.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Storage configuration or the requested write is unavailable.</exception>
    public Task WriteAsync(string blobName, ReadOnlyMemory<byte> data, string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a private object for a previously authorized read.</summary>
    /// <param name="blobName">Application-generated key obtained from authorized ready asset metadata.</param>
    /// <param name="cancellationToken">Cancels the private provider operation.</param>
    /// <returns>A readable stream owned and disposed by the caller.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The private object or storage provider is unavailable.</exception>
    public Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>Removes a known expired temporary object idempotently; it is not a general ready-media retention operation.</summary>
    /// <param name="blobName">Application-generated key of an unattached expired pending or failed upload.</param>
    /// <param name="cancellationToken">Cancels the private provider operation.</param>
    /// <returns>A task completing when the object is confirmed deleted or already absent.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The provider cannot establish successful removal.</exception>
    public Task DeleteIfExistsAsync(string blobName, CancellationToken cancellationToken = default);
}
