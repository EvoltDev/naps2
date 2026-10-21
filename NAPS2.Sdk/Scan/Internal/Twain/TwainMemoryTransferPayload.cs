#if !MACOS
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// How the bytes of a TWAIN memory transfer are to be stored as a raw artifact.
/// </summary>
internal enum JpegDensityPatch
{
    /// <summary>The head was patched; write the patched bytes instead.</summary>
    Patched,

    /// <summary>The application segments are not complete yet; supply more of the stream.</summary>
    NeedMoreData,

    /// <summary>Not a JPEG stream, malformed, or no resolution to apply; write the bytes unchanged.</summary>
    NotApplicable
}

internal enum TwainMemoryPayloadKind
{
    /// <summary>Uncompressed strips, stored as raster blocks with a layout per strip.</summary>
    Raster,

    /// <summary>A JPEG stream split across buffers, stored unchanged as an encoded image.</summary>
    Jpeg,

    /// <summary>A CCITT Group 4 stream, wrapped in a single-strip TIFF so image decoders can read it.</summary>
    Group4Tiff
}

/// <summary>
/// Interprets TWAIN memory transfers whose geometry or compression the source chose, e.g. when the scanner's own UI
/// enabled compression. For compressed transfers the source reports strip geometry as TWON_DONTCARE32 or 0, because
/// rows and columns have no meaning for a compressed stream; the bytes must be stored as an encoded image instead.
/// </summary>
internal static class TwainMemoryTransferPayload
{
    /// <summary>TWON_DONTCARE32: the source does not report this value.</summary>
    public const uint DontCare32 = 0xFFFFFFFF;

    public static TwainMemoryPayloadKind Classify(CompressionType compression) => compression switch
    {
        CompressionType.None => TwainMemoryPayloadKind.Raster,
        CompressionType.Jpeg => TwainMemoryPayloadKind.Jpeg,
        CompressionType.Group4 => TwainMemoryPayloadKind.Group4Tiff,
        _ => throw new NotSupportedException(
            $"The scanner delivered a TWAIN memory transfer with {compression} compression, which is not supported. " +
            "Turn off compression in the scanner settings.")
    };

    /// <summary>
    /// Resolves the geometry of one uncompressed memory strip, falling back to the image info for values the source
    /// left unset (0 or TWON_DONTCARE32).
    /// </summary>
    public static (int Width, int Height, int Stride) ResolveRasterStrip(
        uint columns, uint rows, uint bytesPerRow, int dataLength, int imageWidth, int bitsPerPixel)
    {
        if (!IsKnown(bytesPerRow))
        {
            throw new InvalidOperationException(
                "TWAIN returned an uncompressed memory transfer without a row size.");
        }
        if (!IsKnown(columns))
        {
            if (bitsPerPixel > 0)
            {
                columns = (uint) (bytesPerRow * 8L / bitsPerPixel);
            }
            else if (imageWidth > 0)
            {
                columns = (uint) imageWidth;
            }
            else
            {
                throw new InvalidOperationException(
                    "TWAIN returned an uncompressed memory transfer without a width.");
            }
        }
        if (!IsKnown(rows))
        {
            rows = (uint) (dataLength / bytesPerRow);
        }
        return (checked((int) columns), checked((int) rows), checked((int) bytesPerRow));
    }

    /// <summary>
    /// Builds the header of a little-endian single-strip TIFF for a CCITT Group 4 stream of the given length that
    /// covers the whole image. The stream follows the header directly, so it can be copied after it in chunks
    /// instead of being held in memory.
    /// </summary>
    /// <remarks>
    /// CCITT codes describe white and black runs rather than bit values, so WhiteIsZero only fixes how decoders
    /// store the pixels; the image appears the same either way.
    /// </remarks>
    public static byte[] CreateGroup4TiffHeader(long dataLength, int width, int height, double xRes, double yRes)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"TWAIN returned a Group 4 memory transfer without image dimensions ({width}x{height}).");
        }

        const ushort typeShort = 3;
        const ushort typeLong = 4;
        const ushort typeRational = 5;
        var hasResolution = xRes > 0 && yRes > 0;
        var entryCount = hasResolution ? 12 : 9;
        // Header, directory, resolution values, then the strip.
        const int ifdOffset = 8;
        var resolutionOffset = ifdOffset + 2 + entryCount * 12 + 4;
        var dataOffset = resolutionOffset + (hasResolution ? 16 : 0);
        if (dataLength < 0 || dataOffset + dataLength > uint.MaxValue)
        {
            throw new NotSupportedException(
                $"A Group 4 page of {dataLength} bytes does not fit in a TIFF file.");
        }

        using var stream = new MemoryStream(dataOffset);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) 'I');
        writer.Write((byte) 'I');
        writer.Write((ushort) 42);
        writer.Write((uint) ifdOffset);

        writer.Write((ushort) entryCount);
        // Tags must be in ascending order.
        WriteEntry(256, typeLong, (uint) width); // ImageWidth
        WriteEntry(257, typeLong, (uint) height); // ImageLength
        WriteEntry(258, typeShort, 1); // BitsPerSample
        WriteEntry(259, typeShort, 4); // Compression = CCITT T.6 (Group 4)
        WriteEntry(262, typeShort, 0); // PhotometricInterpretation = WhiteIsZero
        WriteEntry(273, typeLong, (uint) dataOffset); // StripOffsets
        WriteEntry(277, typeShort, 1); // SamplesPerPixel
        WriteEntry(278, typeLong, (uint) height); // RowsPerStrip
        WriteEntry(279, typeLong, (uint) dataLength); // StripByteCounts
        if (hasResolution)
        {
            WriteEntry(282, typeRational, (uint) resolutionOffset); // XResolution
            WriteEntry(283, typeRational, (uint) resolutionOffset + 8); // YResolution
            WriteEntry(296, typeShort, 2); // ResolutionUnit = inch
        }
        writer.Write((uint) 0); // No further IFD

        if (hasResolution)
        {
            WriteRational(xRes);
            WriteRational(yRes);
        }
        writer.Flush();
        return stream.ToArray();

        void WriteEntry(ushort tag, ushort type, uint value)
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write((uint) 1);
            if (type == typeShort)
            {
                // A SHORT value is left-justified in the 4-byte value field.
                writer.Write((ushort) value);
                writer.Write((ushort) 0);
            }
            else
            {
                writer.Write(value);
            }
        }

        void WriteRational(double value)
        {
            writer.Write((uint) Math.Round(value * 100));
            writer.Write((uint) 100);
        }
    }

    /// <summary>
    /// Sets the resolution recorded in the start of a JPEG stream to the TWAIN resolution, without re-encoding.
    /// Decoders and the PDF exporter take the resolution from the JPEG itself, and a scanner's JPEG may carry none
    /// or a different one, in JFIF and/or EXIF (where EXIF takes precedence for some readers). Both are made to
    /// agree: a JFIF APP0 density is replaced or, if there is no APP0, one is inserted after the SOI marker; an
    /// EXIF XResolution/YResolution/ResolutionUnit is overwritten in place.
    /// </summary>
    /// <param name="head">The first bytes of the stream. All application segments (APP0–APP15) that follow the SOI
    /// marker must be complete to patch them; otherwise <see cref="JpegDensityPatch.NeedMoreData"/> is returned.</param>
    /// <param name="patched">The patched replacement for <paramref name="head"/> when the result is
    /// <see cref="JpegDensityPatch.Patched"/>.</param>
    public static JpegDensityPatch TryApplyJpegDensity(ReadOnlySpan<byte> head, double xRes, double yRes,
        out byte[] patched)
    {
        patched = [];
        if (xRes <= 0 || yRes <= 0 || xRes > ushort.MaxValue || yRes > ushort.MaxValue)
        {
            return JpegDensityPatch.NotApplicable;
        }
        if (head.Length < 2)
        {
            return JpegDensityPatch.NeedMoreData;
        }
        if (head[0] != 0xFF || head[1] != 0xD8)
        {
            return JpegDensityPatch.NotApplicable;
        }
        var x = (ushort) Math.Round(xRes);
        var y = (ushort) Math.Round(yRes);

        var copy = head.ToArray();
        var hasJfif = false;
        var position = 2;
        while (true)
        {
            if (position + 4 > copy.Length)
            {
                return JpegDensityPatch.NeedMoreData;
            }
            if (copy[position] != 0xFF)
            {
                return JpegDensityPatch.NotApplicable;
            }
            var marker = copy[position + 1];
            if (marker < 0xE0 || marker > 0xEF)
            {
                // The application segments are done; the frame data that follows never carries a resolution.
                break;
            }
            var length = (copy[position + 2] << 8) | copy[position + 3];
            if (length < 2)
            {
                return JpegDensityPatch.NotApplicable;
            }
            var end = position + 2 + length;
            if (end > copy.Length)
            {
                return JpegDensityPatch.NeedMoreData;
            }
            var data = copy.AsSpan(position + 4, length - 2);
            if (marker == 0xE0 && data.Length >= 12 && data.StartsWith("JFIF\0"u8))
            {
                // "JFIF\0", version (2), units (1), X density (2), Y density (2).
                data[7] = 1; // dots per inch
                WriteBigEndian(data, 8, x);
                WriteBigEndian(data, 10, y);
                hasJfif = true;
            }
            else if (marker == 0xE1 && data.Length >= 14 && data.StartsWith("Exif\0\0"u8))
            {
                PatchExifResolution(data.Slice(6), x, y);
            }
            position = end;
        }

        if (hasJfif)
        {
            patched = copy;
            return JpegDensityPatch.Patched;
        }
        byte[] app0 =
        [
            0xFF, 0xE0, 0x00, 0x10, (byte) 'J', (byte) 'F', (byte) 'I', (byte) 'F', 0x00,
            0x01, 0x01, // version 1.01
            0x01, // dots per inch
            0, 0, 0, 0, // densities, set below
            0x00, 0x00 // no thumbnail
        ];
        WriteBigEndian(app0, 12, x);
        WriteBigEndian(app0, 14, y);
        patched = new byte[copy.Length + app0.Length];
        copy.AsSpan(0, 2).CopyTo(patched);
        app0.CopyTo(patched, 2);
        copy.AsSpan(2).CopyTo(patched.AsSpan(2 + app0.Length));
        return JpegDensityPatch.Patched;

        static void WriteBigEndian(Span<byte> target, int offset, ushort value)
        {
            target[offset] = (byte) (value >> 8);
            target[offset + 1] = (byte) value;
        }
    }

    /// <summary>
    /// Overwrites the resolution tags of the first image directory of an EXIF TIFF structure in place. Tags that are
    /// absent or malformed are left alone; readers then fall back to the JFIF density.
    /// </summary>
    private static void PatchExifResolution(Span<byte> tiff, ushort x, ushort y)
    {
        if (tiff.Length < 8) return;
        bool littleEndian;
        if (tiff[0] == 0x49 && tiff[1] == 0x49) littleEndian = true;
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D) littleEndian = false;
        else return;
        if (Read16(tiff, 2) != 42) return;
        var ifd = Read32(tiff, 4);
        if (ifd < 8 || (long) ifd + 2 > tiff.Length) return;
        var count = Read16(tiff, (int) ifd);
        for (var i = 0; i < count; i++)
        {
            var entry = (int) ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) return;
            var tag = Read16(tiff, entry);
            var type = Read16(tiff, entry + 2);
            if ((tag == 282 || tag == 283) && type == 5) // XResolution / YResolution, RATIONAL
            {
                var valueOffset = Read32(tiff, entry + 8);
                if ((long) valueOffset + 8 > tiff.Length) continue;
                Write32(tiff, (int) valueOffset, tag == 282 ? x : y);
                Write32(tiff, (int) valueOffset + 4, 1);
            }
            else if (tag == 296 && type == 3) // ResolutionUnit, SHORT
            {
                Write16(tiff, entry + 8, 2); // inch
            }
        }

        ushort Read16(Span<byte> b, int o) => littleEndian
            ? (ushort) (b[o] | (b[o + 1] << 8))
            : (ushort) ((b[o] << 8) | b[o + 1]);

        uint Read32(Span<byte> b, int o) => littleEndian
            ? (uint) (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24))
            : (uint) ((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        void Write16(Span<byte> b, int o, ushort v)
        {
            b[o] = (byte) (littleEndian ? v : v >> 8);
            b[o + 1] = (byte) (littleEndian ? v >> 8 : v);
        }

        void Write32(Span<byte> b, int o, uint v)
        {
            for (var k = 0; k < 4; k++)
            {
                b[o + k] = (byte) (v >> (littleEndian ? 8 * k : 8 * (3 - k)));
            }
        }
    }

    private static bool IsKnown(uint value) => value != 0 && value != DontCare32;
}
#endif
