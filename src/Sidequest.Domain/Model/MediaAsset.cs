namespace Sidequest.Domain.Model;

/// <summary>Private Quest media metadata; the blob reference is not a public permanent URL or access grant.</summary>
public sealed class MediaAsset : Entity
{
    /// <summary>Internal Quest identifier whose authorization protects the asset.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal account identifier of the media creator.</summary>
    public Guid CreatedById { get; set; }
    /// <summary>Private storage blob key used only through authorized media delivery.</summary>
    public string BlobName { get; set; } = "";
    /// <summary>Validated media MIME type.</summary>
    public string ContentType { get; set; } = "";
    /// <summary>Stored content size in bytes.</summary>
    public long SizeBytes { get; set; }
    /// <summary>Image width in pixels.</summary>
    public int Width { get; set; }
    /// <summary>Image height in pixels.</summary>
    public int Height { get; set; }
    /// <summary>Upload/readiness state; pending or failed content is not ready for normal delivery.</summary>
    public MediaStatus Status { get; set; }
    /// <summary>UTC instant when the asset record was created.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
}
