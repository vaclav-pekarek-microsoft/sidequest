namespace Sidequest.Application.Media;

/// <summary>Decodes bounded untrusted image input and re-encodes it without source metadata.</summary>
public interface IImageSanitizer
{
    /// <summary>Validates actual JPEG, PNG or WebP content and rejects malformed or oversized input before publishing any result.</summary>
    /// <param name="content">Caller-owned readable input stream, limited to 2,097,152 bytes regardless of declared length or seek support.</param>
    /// <param name="cancellationToken">Cancels reading and cooperative processing boundaries.</param>
    /// <returns>Re-encoded content with validated dimensions whose product does not exceed 20,000,000 pixels.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input is unsupported, malformed, over the byte/pixel limits,
    /// or cannot be processed safely. Filenames and supplied MIME types are not validation evidence.</exception>
    public Task<SanitizedImage> SanitizeAsync(Stream content, CancellationToken cancellationToken = default);
}
