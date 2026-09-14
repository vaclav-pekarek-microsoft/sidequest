namespace Sidequest.Domain.Model;

/// <summary>Private media upload/readiness lifecycle.</summary>
public enum MediaStatus
{
    /// <summary>Upload or preparation has not completed.</summary>
    Pending,
    /// <summary>Validated media is available for authorized delivery.</summary>
    Ready,
    /// <summary>Upload or preparation failed.</summary>
    Failed
}
