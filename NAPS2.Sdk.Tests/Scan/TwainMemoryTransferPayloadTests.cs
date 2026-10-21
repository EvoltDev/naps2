#if !MACOS
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

        var tiff = TwainMemoryTransferPayload.CreateGroup4Tiff(group4, 32, 16, 200, 200);
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

        var tiff = TwainMemoryTransferPayload.CreateGroup4Tiff(group4, 32, 16, 0, 0);
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
            TwainMemoryTransferPayload.CreateGroup4Tiff([0xFF], width, height, 200, 200));
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
