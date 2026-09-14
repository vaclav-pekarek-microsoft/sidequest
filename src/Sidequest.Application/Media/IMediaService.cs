namespace Sidequest.Application.Media;

/// <summary>Manages optional Quest covers through current authorization, optimistic concurrency and validated private storage.</summary>
/// <remarks>Provider I/O occurs outside database transactions. Every operation rechecks current account eligibility
/// and resource access; neither an asset identifier nor administrator status grants content access.</remarks>
public interface IMediaService
{
    /// <summary>Validates and uploads a replacement cover, then attaches it only if current ownership, lifecycle and version still permit editing.</summary>
    /// <param name="questId">Quest whose current eligible owner is uploading the cover.</param>
    /// <param name="expectedVersion">Nonempty opaque Quest rowversion from the editor; concurrent changes are never overwritten.</param>
    /// <param name="content">Caller-owned readable stream containing at most 2,097,152 bytes of actual JPEG, PNG or WebP data.
    /// Decoded content must contain at most 20,000,000 pixels. The service does not dispose this stream.</param>
    /// <param name="cancellationToken">Cancels upload processing without treating an incomplete upload as ready.</param>
    /// <returns>The attached ready asset identifier and updated Quest version.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The image is invalid, access or editing is unavailable,
    /// the version conflicts, or a required provider is unavailable. A failed upload does not replace the prior cover.</exception>
    public Task<CoverUpdate> UploadCoverAsync(Guid questId, byte[] expectedVersion, Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the current cover assignment without granting access to or deleting retained ready media.</summary>
    /// <param name="questId">Quest whose current eligible owner requests removal.</param>
    /// <param name="expectedVersion">Nonempty opaque Quest rowversion protecting against concurrent edits.</param>
    /// <param name="cancellationToken">Cancels authorization and persistence.</param>
    /// <returns>A null cover identifier and the current version after the repeat-safe removal.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access or editing is unavailable, the version is invalid, or it conflicts.</exception>
    public Task<CoverUpdate> RemoveCoverAsync(Guid questId, byte[] expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a ready, currently assigned cover only after reauthorizing the caller against its Quest and parent Event.</summary>
    /// <param name="assetId">Opaque media identifier; pending, failed, detached and unavailable assets are not served.</param>
    /// <param name="moderation">Selects the explicit audited Event-owner moderation path, never a draft or administrator bypass.</param>
    /// <param name="cancellationToken">Cancels authorization and private storage reading.</param>
    /// <returns>Validated encoded image bytes and their MIME type, without exposing a Blob URL or key.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The content is unavailable to the caller or private storage is unavailable.</exception>
    public Task<MediaContent> ReadAsync(Guid assetId, bool moderation = false,
        CancellationToken cancellationToken = default);
}
