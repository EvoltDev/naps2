#if !MACOS
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// How the bytes of a TWAIN memory transfer are to be stored as a raw artifact.
/// </summary>
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
    /// Sets the pixel density in the first buffer of a JPEG stream to the TWAIN resolution, without re-encoding.
    /// Decoders take the resolution from the JPEG itself, and a scanner's JPEG may carry no density or a different
    /// one. An existing JFIF APP0 segment gets its density replaced; otherwise one is inserted after the SOI marker.
    /// </summary>
    /// <returns>False when the resolution is unknown or the buffer does not start a JPEG stream; the buffer is then
    /// stored unchanged.</returns>
    public static bool TryApplyJfifDensity(ReadOnlySpan<byte> firstBuffer, double xRes, double yRes,
        out byte[] patched)
    {
        patched = [];
        if (xRes <= 0 || yRes <= 0 || xRes > ushort.MaxValue || yRes > ushort.MaxValue)
        {
            return false;
        }
        if (firstBuffer.Length < 2 || firstBuffer[0] != 0xFF || firstBuffer[1] != 0xD8)
        {
            return false;
        }
        var x = (ushort) Math.Round(xRes);
        var y = (ushort) Math.Round(yRes);

        if (firstBuffer.Length >= 18 && firstBuffer[2] == 0xFF && firstBuffer[3] == 0xE0 &&
            firstBuffer.Slice(6, 5).SequenceEqual("JFIF\0"u8))
        {
            // SOI, APP0 marker, length, "JFIF\0", version (2), units (1), X density (2), Y density (2).
            patched = firstBuffer.ToArray();
            patched[13] = 1; // dots per inch
            WriteBigEndian(patched, 14, x);
            WriteBigEndian(patched, 16, y);
            return true;
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
        patched = new byte[firstBuffer.Length + app0.Length];
        firstBuffer.Slice(0, 2).CopyTo(patched);
        app0.CopyTo(patched, 2);
        firstBuffer.Slice(2).CopyTo(patched.AsSpan(2 + app0.Length));
        return true;

        static void WriteBigEndian(byte[] target, int offset, ushort value)
        {
            target[offset] = (byte) (value >> 8);
            target[offset + 1] = (byte) value;
        }
    }

    private static bool IsKnown(uint value) => value != 0 && value != DontCare32;
}
#endif
