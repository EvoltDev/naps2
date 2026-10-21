#if !MACOS
using NAPS2.Pdf;
using NAPS2.Scan.Internal.Twain;
using NAPS2.Sdk.Tests.Asserts;
using NTwain.Data;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class TwainMemoryTransferPayloadTests : ContextualTests
{
    private const uint DontCare = TwainMemoryTransferPayload.DontCare32;

    [Theory]
    [InlineData(CompressionType.None, nameof(TwainMemoryPayloadKind.Raster))]
    [InlineData(CompressionType.Jpeg, nameof(TwainMemoryPayloadKind.Jpeg))]
    [InlineData(CompressionType.Group4, nameof(TwainMemoryPayloadKind.Group4Tiff))]
    public void Classify_MapsSupportedCompressions(CompressionType compression, string expected)
    {
        Assert.Equal(expected, TwainMemoryTransferPayload.Classify(compression).ToString());
    }

    [Theory]
    [InlineData(CompressionType.Group31D)]
    [InlineData(CompressionType.Lzw)]
    [InlineData(CompressionType.Png)]
    public void Classify_RejectsOtherCompressionsByName(CompressionType compression)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TwainMemoryTransferPayload.Classify(compression));
        Assert.Contains(compression.ToString(), ex.Message);
    }

    [Fact]
    public void ResolveRasterStrip_KeepsReportedGeometry()
    {
        var geometry = TwainMemoryTransferPayload.ResolveRasterStrip(
            columns: 100, rows: 10, bytesPerRow: 300, dataLength: 3000, imageWidth: 100, bitsPerPixel: 24);

        Assert.Equal((100, 10, 300), geometry);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(DontCare)]
    public void ResolveRasterStrip_DerivesUnsetColumnsFromRowSize(uint columns)
    {
        var geometry = TwainMemoryTransferPayload.ResolveRasterStrip(
            columns, rows: 10, bytesPerRow: 300, dataLength: 3000, imageWidth: 0, bitsPerPixel: 24);

        Assert.Equal((100, 10, 300), geometry);
    }

    [Fact]
    public void ResolveRasterStrip_FallsBackToImageWidthWithoutBitDepth()
    {
        var geometry = TwainMemoryTransferPayload.ResolveRasterStrip(
            DontCare, rows: 10, bytesPerRow: 300, dataLength: 3000, imageWidth: 98, bitsPerPixel: 0);

        Assert.Equal((98, 10, 300), geometry);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(DontCare)]
    public void ResolveRasterStrip_DerivesUnsetRowsFromDataLength(uint rows)
    {
        var geometry = TwainMemoryTransferPayload.ResolveRasterStrip(
            columns: 100, rows, bytesPerRow: 300, dataLength: 3000, imageWidth: 100, bitsPerPixel: 24);

        Assert.Equal((100, 10, 300), geometry);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(DontCare)]
    public void ResolveRasterStrip_RejectsUnsetRowSize(uint bytesPerRow)
    {
        Assert.Throws<InvalidOperationException>(() => TwainMemoryTransferPayload.ResolveRasterStrip(
            columns: 100, rows: 10, bytesPerRow, dataLength: 3000, imageWidth: 100, bitsPerPixel: 24));
    }

    [Fact]
    public void CreateGroup4Tiff_DecodesBlackOnWhite()
    {
        var group4 = EncodeGroup4(32, 16, (8, 4), (20, 10));

        var tiff = WrapGroup4(group4, 32, 16, 200, 200);
        using var image = ImageContext.Load(new MemoryStream(tiff));

        Assert.Equal(32, image.Width);
        Assert.Equal(16, image.Height);
        Assert.Equal(200, image.HorizontalResolution, 1);
        ImageAsserts.PixelColors(image, new()
        {
            { (0, 0), (0xFF, 0xFF, 0xFF) },
            { (8, 4), (0, 0, 0) },
            { (20, 10), (0, 0, 0) },
            { (31, 15), (0xFF, 0xFF, 0xFF) }
        });
    }

    [Fact]
    public void CreateGroup4Tiff_OmitsUnknownResolution()
    {
        var group4 = EncodeGroup4(32, 16);

        var tiff = WrapGroup4(group4, 32, 16, 0, 0);
        using var image = ImageContext.Load(new MemoryStream(tiff));

        Assert.Equal(32, image.Width);
        Assert.Equal(16, image.Height);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(8, -1)]
    public void CreateGroup4Tiff_RejectsUnknownDimensions(int width, int height)
    {
        Assert.Throws<InvalidOperationException>(() =>
            TwainMemoryTransferPayload.CreateGroup4TiffHeader(1, width, height, 200, 200));
    }

    [Fact]
    public void ApplyJpegDensity_ReplacesJfifDensity()
    {
        var jpeg = EncodeJpeg(72);
        Assert.Equal(0xE0, jpeg[3]); // ImageSharp writes a JFIF APP0 segment

        Assert.Equal(JpegDensityPatch.Patched,
            TwainMemoryTransferPayload.TryApplyJpegDensity(jpeg, 200, 300, out var patched));

        Assert.Equal(jpeg.Length, patched.Length);
        AssertResolution(patched, 200, 300);
    }

    [Fact]
    public void ApplyJpegDensity_OverridesExifResolution()
    {
        // EXIF only, 72 dpi. EXIF takes precedence over JFIF in the PDF exporter's JPEG reader, so it must be
        // patched too, not just covered by an inserted JFIF segment.
        var jpeg = ImageResources.dog_exif;
        Assert.Equal(72, JpegFormatHelper.ReadHeader(new MemoryStream(jpeg))!.HorizontalDpi);

        Assert.Equal(JpegDensityPatch.Patched,
            TwainMemoryTransferPayload.TryApplyJpegDensity(jpeg, 200, 300, out var patched));

        AssertResolution(patched, 200, 300);
        Assert.True(JpegFormatHelper.ReadHeader(new MemoryStream(patched))!.HasExifHeader);
    }

    [Fact]
    public void ApplyJpegDensity_InsertsJfifSegmentWhenMissing()
    {
        var withApp0 = EncodeJpeg(72);
        var app0Length = (withApp0[4] << 8) | withApp0[5];
        // A JPEG without APP0: SOI followed directly by the next segment.
        byte[] jpeg = [..withApp0.AsSpan(0, 2), ..withApp0.AsSpan(4 + app0Length)];

        Assert.Equal(JpegDensityPatch.Patched,
            TwainMemoryTransferPayload.TryApplyJpegDensity(jpeg, 200, 300, out var patched));

        Assert.Equal(jpeg.Length + 18, patched.Length);
        AssertResolution(patched, 200, 300);
    }

    [Fact]
    public void ApplyJpegDensity_AsksForMoreDataUntilApplicationSegmentsAreComplete()
    {
        // The sink feeds TWAIN buffers until the header is complete; an EXIF segment can span buffers.
        var jpeg = ImageResources.dog_exif;
        var headerEnd = ApplicationSegmentsEnd(jpeg);
        Assert.True(headerEnd > 4 + ((jpeg[4] << 8) | jpeg[5])); // more application segments follow EXIF

        Assert.Equal(JpegDensityPatch.NeedMoreData,
            TwainMemoryTransferPayload.TryApplyJpegDensity(jpeg.AsSpan(0, 1), 200, 200, out _));
        Assert.Equal(JpegDensityPatch.NeedMoreData,
            TwainMemoryTransferPayload.TryApplyJpegDensity(jpeg.AsSpan(0, headerEnd - 1), 200, 200, out _));
        Assert.Equal(JpegDensityPatch.Patched,
            TwainMemoryTransferPayload.TryApplyJpegDensity(jpeg.AsSpan(0, headerEnd + 4), 200, 200, out var head));

        // The patched head followed by the untouched rest of the stream is a valid JPEG.
        byte[] stream = [..head, ..jpeg.AsSpan(headerEnd + 4)];
        AssertResolution(stream, 200, 200);
    }

    /// <summary>
    /// Returns the offset just past the application segments (APP0–APP15) that follow the SOI marker.
    /// </summary>
    private static int ApplicationSegmentsEnd(byte[] jpeg)
    {
        var position = 2;
        while (jpeg[position + 1] is >= 0xE0 and <= 0xEF)
        {
            position += 2 + ((jpeg[position + 2] << 8) | jpeg[position + 3]);
        }
        return position;
    }

    [Fact]
    public void ApplyJpegDensity_LeavesNonJpegDataAndUnknownResolutionAlone()
    {
        var jpeg = EncodeJpeg(72);

        Assert.Equal(JpegDensityPatch.NotApplicable,
            TwainMemoryTransferPayload.TryApplyJpegDensity(jpeg, 0, 0, out _));
        Assert.Equal(JpegDensityPatch.NotApplicable,
            TwainMemoryTransferPayload.TryApplyJpegDensity([0x00, 0x01, 0x02], 200, 200, out _));
    }

    /// <summary>
    /// Checks the resolution through the PDF exporter's JPEG reader (which reads JFIF and EXIF, EXIF last) and
    /// through the image decoder. The ImageSharp backend reports the horizontal resolution for both axes, so the
    /// decoder is only checked horizontally.
    /// </summary>
    private void AssertResolution(byte[] jpeg, double xDpi, double yDpi)
    {
        var header = JpegFormatHelper.ReadHeader(new MemoryStream(jpeg));
        Assert.NotNull(header);
        Assert.Equal(xDpi, header.HorizontalDpi, 1);
        Assert.Equal(yDpi, header.VerticalDpi, 1);
        using var image = ImageContext.Load(new MemoryStream(jpeg));
        Assert.Equal(xDpi, image.HorizontalResolution, 1);
    }

    /// <summary>
    /// Builds the TIFF the sink produces: the header followed by the Group 4 stream.
    /// </summary>
    private static byte[] WrapGroup4(byte[] group4, int width, int height, double xRes, double yRes) =>
    [
        ..TwainMemoryTransferPayload.CreateGroup4TiffHeader(group4.Length, width, height, xRes, yRes),
        ..group4
    ];

    private static byte[] EncodeJpeg(double dpi)
    {
        using var source = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(16, 16);
        source.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
        source.Metadata.HorizontalResolution = dpi;
        source.Metadata.VerticalResolution = dpi;
        using var encoded = new MemoryStream();
        source.Save(encoded, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
        return encoded.ToArray();
    }

    /// <summary>
    /// Produces the CCITT Group 4 stream a scanner would deliver for a white page with the given black pixels, by
    /// encoding a TIFF with ImageSharp and taking its single strip.
    /// </summary>
    private static byte[] EncodeGroup4(int width, int height, params (int X, int Y)[] blackPixels)
    {
        using var source = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.L8>(
            width, height, new SixLabors.ImageSharp.PixelFormats.L8(0xFF));
        foreach (var (x, y) in blackPixels)
        {
            source[x, y] = new SixLabors.ImageSharp.PixelFormats.L8(0);
        }
        using var encoded = new MemoryStream();
        source.Save(encoded,
            new SixLabors.ImageSharp.Formats.Tiff.TiffEncoder
            {
                BitsPerPixel = SixLabors.ImageSharp.Formats.Tiff.TiffBitsPerPixel.Bit1,
                Compression = SixLabors.ImageSharp.Formats.Tiff.Constants.TiffCompression.CcittGroup4Fax,
                PhotometricInterpretation =
                    SixLabors.ImageSharp.Formats.Tiff.Constants.TiffPhotometricInterpretation.WhiteIsZero
            });

        var tiff = encoded.ToArray();
        Assert.Equal((byte) 'I', tiff[0]);
        var ifd = (int) BitConverter.ToUInt32(tiff, 4);
        var entryCount = BitConverter.ToUInt16(tiff, ifd);
        var tags = new Dictionary<ushort, (ushort Type, uint Count, uint Value)>();
        for (var i = 0; i < entryCount; i++)
        {
            var entry = ifd + 2 + i * 12;
            var type = BitConverter.ToUInt16(tiff, entry + 2);
            var value = type == 3 ? BitConverter.ToUInt16(tiff, entry + 8) : BitConverter.ToUInt32(tiff, entry + 8);
            tags[BitConverter.ToUInt16(tiff, entry)] = (type, BitConverter.ToUInt32(tiff, entry + 4), value);
        }
        Assert.Equal(4u, tags[259].Value); // Group 4
        Assert.Equal(0u, tags[262].Value); // WhiteIsZero: fax polarity, as TWAIN delivers it
        Assert.Equal(1u, tags[273].Count); // single strip
        return tiff.AsSpan((int) tags[273].Value, (int) tags[279].Value).ToArray();
    }
}
#endif
