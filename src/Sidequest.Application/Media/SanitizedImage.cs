namespace Sidequest.Application.Media;

/// <summary>Describes a decoded and re-encoded image produced by the trusted sanitizer boundary.</summary>
/// <param name="Data">Encoded image bytes without untrusted source metadata.</param>
/// <param name="ContentType">Actual output JPEG, PNG or WebP MIME type.</param>
/// <param name="Width">Positive decoded image width in pixels.</param>
/// <param name="Height">Positive decoded image height in pixels; the width/height product is at most 20,000,000.</param>
public sealed record SanitizedImage(byte[] Data, string ContentType, int Width, int Height);
