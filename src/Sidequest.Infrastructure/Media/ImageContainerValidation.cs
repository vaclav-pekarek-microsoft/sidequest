using System.Buffers.Binary;
using SkiaSharp;
using Sidequest.Domain.Rules;

namespace Sidequest.Infrastructure.Media;

internal static class ImageContainerValidation
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    // Native decoders may intentionally recover pixels from broken containers (for example PNG without IEND).
    // Verify framing as well as full native raster decoding; this is not a substitute for the latter.
    internal static void Validate(ReadOnlySpan<byte> input, SKEncodedImageFormat format, CancellationToken token)
    {
        if (format == SKEncodedImageFormat.Png)
            ValidatePng(input, token);
        if (format == SKEncodedImageFormat.Webp)
            ValidateWebp(input);
    }

    private static void ValidatePng(ReadOnlySpan<byte> input, CancellationToken token)
    {
        var offset = 8;
        var hasPixels = false;
        while (input.Length - offset >= 12)
        {
            token.ThrowIfCancellationRequested();
            var length = BinaryPrimitives.ReadUInt32BigEndian(input[offset..]);
            if (length > input.Length - offset - 12)
                throw Invalid();
            var typeAndData = input.Slice(offset + 4, (int)length + 4);
            var type = typeAndData[..4];
            var expected = BinaryPrimitives.ReadUInt32BigEndian(input[(offset + 8 + (int)length)..]);
            uint crc = uint.MaxValue;
            for (var index = 0; index < typeAndData.Length; index++)
            {
                if ((index & 16383) == 0)
                    token.ThrowIfCancellationRequested();
                crc = (crc >> 8) ^ CrcTable[(crc ^ typeAndData[index]) & 255];
            }
            if (~crc != expected || offset == 8 && (!type.SequenceEqual("IHDR"u8) || length != 13) ||
                offset != 8 && type.SequenceEqual("IHDR"u8) || type.SequenceEqual("acTL"u8))
                throw Invalid();
            if (type.SequenceEqual("IDAT"u8))
                hasPixels |= length != 0;
            if (type.SequenceEqual("IEND"u8))
            {
                if (!hasPixels || length != 0)
                    throw Invalid();
                return;
            }
            offset += (int)length + 12;
        }
        throw Invalid();
    }

    private static void ValidateWebp(ReadOnlySpan<byte> input)
    {
        if (input.Length < 20 || !input[..4].SequenceEqual("RIFF"u8) || !input.Slice(8, 4).SequenceEqual("WEBP"u8))
            throw Invalid();
        var declared = (long)BinaryPrimitives.ReadUInt32LittleEndian(input[4..]) + 8;
        if (declared > input.Length || declared < 20)
            throw Invalid();
        var offset = 12L;
        while (declared - offset >= 8)
        {
            var chunk = input[(int)offset..];
            if (chunk[..4].SequenceEqual("ANIM"u8))
                throw Invalid();
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            offset += 8L + length + (length & 1);
            if (offset > declared)
                throw Invalid();
        }
        if (offset != declared)
            throw Invalid();
    }

    private static uint[] CreateCrcTable()
    {
        var values = new uint[256];
        for (uint index = 0; index < values.Length; index++)
        {
            var crc = index;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            values[index] = crc;
        }
        return values;
    }

    private static DomainException Invalid() => new(ErrorCode.Validation,
        "The image container is incomplete, malformed or animated.", "Cover");
}
