namespace Sidequest.Application.Media;

/// <summary>Contains a sanitized image for authorized delivery, not a public storage reference.</summary>
/// <param name="Data">Fully encoded validated image bytes, with untrusted source metadata stripped.</param>
/// <param name="ContentType">Validated JPEG, PNG or WebP MIME type matching the actual encoded bytes.</param>
public sealed record MediaContent(byte[] Data, string ContentType);
