using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Media;

namespace Sidequest.UnitTests.SecondaryMedia;

/// <summary>Exercises the real native decoder with actual rasters, corrupt compressed data and exact resource boundaries.</summary>
public sealed class SkiaImageSanitizerTests
{
    /// <summary>Every accepted source codec produces independently decodable PNG with the expected dimensions.</summary>
    /// <param name="format">Actual encoded source format, independent of filenames or MIME claims.</param>
    /// <returns>Completion after native decode and normalized-content assertions.</returns>
    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public async Task AcceptedFormats_ProducePngAndPreserveDimensions(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(17, 11);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var source = bitmap.Encode(format, 100);
        using var stream = new MemoryStream(source.ToArray());
        var result = await new SkiaImageSanitizer().SanitizeAsync(stream);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal((17, 11), (result.Width, result.Height));
        using var decoded = SKBitmap.Decode(result.Data);
        Assert.Equal((17, 11), (decoded.Width, decoded.Height));
        Assert.InRange(decoded.GetPixel(0, 0).Blue, (byte)235, (byte)240);
        Assert.True(stream.CanRead);
    }

    /// <summary>The exact 2 MiB source boundary is accepted even when stream length cannot be trusted.</summary>
    /// <param name="seekable">Whether the stream falsely advertises a one-byte length or disallows seeking entirely.</param>
    /// <param name="extra">Bytes beyond the inclusive input boundary.</param>
    /// <returns>Completion after actual bytes, rather than Length, determine acceptance.</returns>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    public async Task ActualByteLimit_IgnoresLyingAndNonseekableLength(bool seekable, int extra)
    {
        var bytes = new byte[2_097_152 + extra];
        Png(1, 1).CopyTo(bytes, 0);
        using var stream = new MisleadingStream(bytes, seekable);
        if (extra == 0)
        {
            var result = await new SkiaImageSanitizer().SanitizeAsync(stream);
            Assert.Equal((1, 1), (result.Width, result.Height));
            Assert.True(result.Data.Length < 2_097_152);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => new SkiaImageSanitizer().SanitizeAsync(stream));
            Assert.Equal(ErrorCode.Validation, error.Code);
            Assert.Equal("Cover", error.Field);
        }
        Assert.True(stream.CanRead);
    }

    /// <summary>A valid 20,000,000-pixel PNG succeeds; one larger dimension product is rejected.</summary>
    /// <param name="height">Decoded raster height; width is 5000 pixels.</param>
    /// <returns>Completion after pixel-boundary and independently decoded output checks.</returns>
    [Theory]
    [InlineData(4000)]
    [InlineData(4001)]
    public async Task PixelLimit_IsInclusiveBeforeRasterAllocation(int height)
    {
        using var input = new MemoryStream(Png(5000, height));
        if (height == 4000)
        {
            var result = await new SkiaImageSanitizer().SanitizeAsync(input);
            Assert.Equal(20_000_000, (long)result.Width * result.Height);
            using var decoded = SKBitmap.Decode(result.Data);
            Assert.Equal(5000, decoded.Width);
            Assert.Equal(4000, decoded.Height);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => new SkiaImageSanitizer().SanitizeAsync(input));
            Assert.Equal(ErrorCode.Validation, error.Code);
        }
    }

    /// <summary>Ancillary text survives in the source but not the independently parsed normalized PNG chunks.</summary>
    /// <returns>Completion after metadata removal and pixel preservation checks.</returns>
    [Fact]
    public async Task Reencoding_StripsMetadataAndRetainsPixel()
    {
        var source = Png(2, 3, metadata: true);
        Assert.Contains("sensitive-location", Encoding.ASCII.GetString(source));
        using var input = new MemoryStream(source);
        var result = await new SkiaImageSanitizer().SanitizeAsync(input);
        Assert.DoesNotContain("sensitive-location", Encoding.ASCII.GetString(result.Data));
        Assert.DoesNotContain("tEXt", Encoding.ASCII.GetString(result.Data));
        using var decoded = SKBitmap.Decode(result.Data);
        Assert.Equal(new SKColor(0, 0, 0, 255), decoded.GetPixel(1, 2));
    }

    /// <summary>Incomplete compressed payloads, empty data and unsupported content never produce a sanitized result.</summary>
    /// <param name="kind">Malformed or unsupported content partition.</param>
    /// <returns>Completion after safe field-specific rejection.</returns>
    [Theory]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("svg")]
    [InlineData("gif")]
    [InlineData("signature")]
    public async Task InvalidContent_IsRejectedAfterRealDecode(string kind)
    {
        var valid = Png(128, 128);
        var bytes = kind switch
        {
            "truncated" => valid[..(valid.Length / 2)],
            "empty" => [],
            "svg" => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"),
            "gif" => Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="),
            _ => valid[..33]
        };
        using var input = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<DomainException>(() => new SkiaImageSanitizer().SanitizeAsync(input));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("Cover", error.Field);
    }

    /// <summary>Cancellation preserves ownership of the supplied stream and frees processing capacity for later input.</summary>
    /// <returns>Completion after cancellation and a subsequent successful operation.</returns>
    [Fact]
    public async Task Cancellation_DoesNotDisposeCallerStreamOrLeakSlot()
    {
        using var input = new MemoryStream(Png(1, 1));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SkiaImageSanitizer().SanitizeAsync(input, cancelled.Token));
        Assert.True(input.CanRead);
        var result = await new SkiaImageSanitizer().SanitizeAsync(input);
        Assert.Equal(1, result.Width);
    }

    /// <summary>Missing required end markers are rejected even if the codec can recover all raster rows.</summary>
    /// <param name="format">Source format whose required final marker is removed.</param>
    /// <returns>Completion after malformed complete-raster input is rejected.</returns>
    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    public async Task MissingEndMarker_IsNotAcceptedAsCompleteImage(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Blue);
        using var encoded = bitmap.Encode(format, 100);
        var bytes = encoded.ToArray();
        using var input = new MemoryStream(bytes[..^(format == SKEncodedImageFormat.Png ? 12 : 2)]);
        var error = await Assert.ThrowsAsync<DomainException>(() => new SkiaImageSanitizer().SanitizeAsync(input));
        Assert.Equal(ErrorCode.Validation, error.Code);
    }

    /// <summary>The first pixel beyond the maximum is rejected before any result can be published.</summary>
    /// <returns>Completion after a highly compressed 20,000,001-pixel image fails validation.</returns>
    [Fact]
    public async Task PixelLimit_RejectsExactlyOnePixelOver()
    {
        using var input = new MemoryStream(Png(20_000_001, 1));
        var error = await Assert.ThrowsAsync<DomainException>(() => new SkiaImageSanitizer().SanitizeAsync(input));
        Assert.Equal(ErrorCode.Validation, error.Code);
    }

    /// <summary>Two blocked readers consume the nonqueued processing budget; excess work is rejected and cancellation releases both slots.</summary>
    /// <returns>Completion after a third admission fails without reading and later processing succeeds.</returns>
    [Fact]
    public async Task ConcurrentProcessing_IsBoundedAndCancellationReleasesSlots()
    {
        using var token = new CancellationTokenSource();
        using var first = new BlockingStream();
        using var second = new BlockingStream();
        var sanitizer = new SkiaImageSanitizer();
        var one = sanitizer.SanitizeAsync(first, token.Token);
        var two = sanitizer.SanitizeAsync(second, token.Token);
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            using var extra = new MemoryStream(Png(1, 1));
            var failure = await Assert.ThrowsAsync<DomainException>(() => sanitizer.SanitizeAsync(extra));
            Assert.Equal(ErrorCode.DependencyUnavailable, failure.Code);
            Assert.Equal(0, extra.Position);
        }
        finally
        {
            await token.CancelAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => one);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => two);
        using var valid = new MemoryStream(Png(1, 1));
        Assert.Equal(1, (await sanitizer.SanitizeAsync(valid)).Width);
    }

    /// <summary>Corrupt ancillary CRCs are rejected rather than relying on permissive decoder metadata recovery.</summary>
    /// <returns>Completion after container-integrity validation rejects an otherwise decodable pixel payload.</returns>
    [Fact]
    public async Task CorruptMetadataCrc_IsRejectedInsteadOfSilentlyRecovered()
    {
        var bytes = Png(1, 1, metadata: true);
        var text = Encoding.ASCII.GetString(bytes).IndexOf("sensitive-location", StringComparison.Ordinal);
        bytes[text] ^= 1;
        using var input = new MemoryStream(bytes);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            new SkiaImageSanitizer().SanitizeAsync(input))).Code);
    }

    /// <summary>All eight real JPEG EXIF orientations become correctly placed pixels, with swapped dimensions where required and no retained orientation metadata.</summary>
    /// <param name="orientation">The TIFF orientation tag value embedded in an actual JPEG APP1 segment.</param>
    /// <param name="width">Expected normalized PNG width.</param>
    /// <param name="height">Expected normalized PNG height.</param>
    /// <param name="topLeft">Source corner index expected at the output's top left.</param>
    /// <param name="topRight">Source corner index expected at the output's top right.</param>
    /// <param name="bottomLeft">Source corner index expected at the output's bottom left.</param>
    /// <param name="bottomRight">Source corner index expected at the output's bottom right.</param>
    /// <returns>Completion after native EXIF parsing, independent corner ordering, dimensions and metadata removal are verified.</returns>
    [Theory]
    [InlineData(1, 80, 48, 0, 1, 2, 3)]
    [InlineData(2, 80, 48, 1, 0, 3, 2)]
    [InlineData(3, 80, 48, 3, 2, 1, 0)]
    [InlineData(4, 80, 48, 2, 3, 0, 1)]
    [InlineData(5, 48, 80, 0, 2, 1, 3)]
    [InlineData(6, 48, 80, 2, 0, 3, 1)]
    [InlineData(7, 48, 80, 3, 1, 2, 0)]
    [InlineData(8, 48, 80, 1, 3, 0, 2)]
    public async Task JpegExifOrientation_IsAppliedBeforeMetadataFreePngEncoding(int orientation,
        int width, int height, int topLeft, int topRight, int bottomLeft, int bottomRight)
    {
        using var bitmap = new SKBitmap(80, 48);
        SKColor[] colors = [SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow];
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, colors[(y < 24 ? 0 : 2) + (x < 40 ? 0 : 1)]);
        using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
        using var baseline = SKBitmap.Decode(jpeg.ToArray());
        SKColor[] sourceCorners =
        [
            baseline.GetPixel(0, 0), baseline.GetPixel(79, 0),
            baseline.GetPixel(0, 47), baseline.GetPixel(79, 47)
        ];
        Assert.Equal(4, sourceCorners.Distinct().Count());
        var bytes = WithExifOrientation(jpeg.ToArray(), orientation);
        using var sourceData = SKData.CreateCopy(bytes);
        using var sourceCodec = SKCodec.Create(sourceData);
        Assert.Equal((SKEncodedOrigin)orientation, sourceCodec.EncodedOrigin);
        using var input = new MemoryStream(bytes);
        var result = await new SkiaImageSanitizer().SanitizeAsync(input);
        Assert.Equal((width, height), (result.Width, result.Height));
        Assert.Equal("image/png", result.ContentType);
        using var output = SKBitmap.Decode(result.Data);
        Assert.Equal((width, height), (output.Width, output.Height));
        Assert.Equal(sourceCorners[topLeft], output.GetPixel(0, 0));
        Assert.Equal(sourceCorners[topRight], output.GetPixel(width - 1, 0));
        Assert.Equal(sourceCorners[bottomLeft], output.GetPixel(0, height - 1));
        Assert.Equal(sourceCorners[bottomRight], output.GetPixel(width - 1, height - 1));
        using var outputData = SKData.CreateCopy(result.Data);
        using var outputCodec = SKCodec.Create(outputData);
        Assert.Equal(SKEncodedOrigin.TopLeft, outputCodec.EncodedOrigin);
        Assert.DoesNotContain("Exif", Encoding.ASCII.GetString(result.Data));
        Assert.DoesNotContain("eXIf", Encoding.ASCII.GetString(result.Data));
    }

    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] exif =
        [
            0x45, 0x78, 0x69, 0x66, 0, 0,
            0x49, 0x49, 0x2a, 0, 8, 0, 0, 0,
            1, 0,
            0x12, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
            0, 0, 0, 0
        ];
        using var result = new MemoryStream();
        result.Write(jpeg.AsSpan(0, 2));
        result.Write([0xff, 0xe1, 0, checked((byte)(exif.Length + 2))]);
        result.Write(exif);
        result.Write(jpeg.AsSpan(2));
        return result.ToArray();
    }

    private static byte[] Png(int width, int height, bool metadata = false)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(output, "IHDR", header);
        if (metadata)
            Chunk(output, "tEXt", Encoding.ASCII.GetBytes("Comment\0sensitive-location"));
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
                zip.Write(row);
        }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(body);
        uint crc = uint.MaxValue;
        foreach (var value in body)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
    }

    private sealed class MisleadingStream(byte[] bytes, bool seekable) : MemoryStream(bytes)
    {
        /// <inheritdoc />
        public override bool CanSeek => seekable;
        /// <inheritdoc />
        public override long Length => seekable ? 1 : throw new NotSupportedException();
    }

    private sealed class BlockingStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
