using System.Runtime.InteropServices;
using SkiaSharp;
using Sidequest.Application.Media;
using Sidequest.Domain.Rules;

namespace Sidequest.Infrastructure.Media;

/// <summary>Fully decodes JPEG, PNG and WebP into a bounded raster and emits metadata-free, lossless PNG.</summary>
/// <remarks>Two process-wide nonqueued slots each permit a decoded and an orientation-corrected 20-megapixel RGBA raster:
/// at most 320,000,000 native raster bytes across both slots, excluding encoded buffers and codec working memory.
/// Animated inputs are rejected. Encoded orientation is applied to pixels before source metadata and profiles are stripped.
/// Cancellation is cooperative before/after synchronous bounded native operations, never unsafe native interruption.</remarks>
public sealed class SkiaImageSanitizer : IImageSanitizer
{
    private const int MaximumBytes = 2_097_152;
    private const long MaximumPixels = 20_000_000;
    private static readonly SemaphoreSlim Slots = new(2, 2);

    /// <inheritdoc />
    public async Task<SanitizedImage> SanitizeAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
            throw Invalid();
        if (!await Slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new DomainException(ErrorCode.DependencyUnavailable, "Image processing is busy. Try again shortly.");
        try
        {
            using var source = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var count = await content.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, MaximumBytes + 1L - source.Length)), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;
                source.Write(buffer, 0, count);
                if (source.Length > MaximumBytes)
                    throw Invalid();
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var data = SKData.CreateCopy(source.GetBuffer().AsSpan(0, checked((int)source.Length)));
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
                throw Invalid();
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaximumPixels ||
                codec.FrameCount > 1)
                throw Invalid();
            ImageContainerValidation.Validate(source.GetBuffer().AsSpan(0, checked((int)source.Length)),
                codec.EncodedFormat, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var raster = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(raster);
            if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(raster, bitmap.GetPixels()) != SKCodecResult.Success)
                throw Invalid();
            cancellationToken.ThrowIfCancellationRequested();
            using var oriented = ApplyOrientation(bitmap, codec.EncodedOrigin, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = oriented ?? bitmap;
            // Share the completed raster with SKImage rather than allocating another mutable-bitmap copy.
            normalized.SetImmutable();
            using var image = SKImage.FromBitmap(normalized);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null || encoded.Size is <= 0 or > 84_000_000)
                throw Invalid();
            cancellationToken.ThrowIfCancellationRequested();
            return new(encoded.ToArray(), "image/png", image.Width, image.Height);
        }
        finally
        {
            Slots.Release();
        }
    }

    private static SKBitmap? ApplyOrientation(SKBitmap source, SKEncodedOrigin origin, CancellationToken token)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return null;
        if (!Enum.IsDefined(origin))
            throw Invalid();
        var transpose = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = transpose ? source.Height : source.Width;
        var height = transpose ? source.Width : source.Height;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            if (result.GetPixels() == IntPtr.Zero)
                throw Invalid();
            var stride = result.RowBytes / sizeof(uint);
            // Map the source's origin and unit axes into the destination. Integer pixel copies avoid
            // resampling and float-coordinate precision loss, including unusually wide legal images.
            var (start, columnStep, rowStep) = origin switch
            {
                SKEncodedOrigin.TopRight => (width - 1, -1, stride),
                SKEncodedOrigin.BottomRight => ((height - 1) * stride + width - 1, -1, -stride),
                SKEncodedOrigin.BottomLeft => ((height - 1) * stride, 1, -stride),
                SKEncodedOrigin.LeftTop => (0, stride, 1),
                SKEncodedOrigin.RightTop => (width - 1, stride, -1),
                SKEncodedOrigin.RightBottom => ((height - 1) * stride + width - 1, -stride, -1),
                SKEncodedOrigin.LeftBottom => ((height - 1) * stride, -stride, 1),
                _ => throw Invalid()
            };
            var input = MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan());
            var output = MemoryMarshal.Cast<byte, uint>(result.GetPixelSpan());
            var inputStride = source.RowBytes / sizeof(uint);
            for (var row = 0; row < source.Height; row++)
            {
                token.ThrowIfCancellationRequested();
                var target = start + row * rowStep;
                var inputOffset = row * inputStride;
                for (var column = 0; column < source.Width; column++, target += columnStep)
                {
                    if ((column & 16383) == 0)
                        token.ThrowIfCancellationRequested();
                    output[target] = input[inputOffset + column];
                }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static DomainException Invalid() => new(ErrorCode.Validation,
        "Choose a complete, nonanimated JPEG, PNG or WebP image up to 2 MiB and 20 megapixels.", "Cover");
}
