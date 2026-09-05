using NAPS2.Images;
using NAPS2.Scan;
using NAPS2.Sdk.Tests.Asserts;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class RawScanDecoderRasterTests : ContextualTests
{
    [Fact]
    public async Task DecodesSingleStripWithRowPadding()
    {
        var payload = new byte[] { 10, 20, 0xEE, 0xEE, 30, 40, 0xDD, 0xDD };
        var artifact = CreateArtifact(
            "raster-padded.raw",
            payload,
            new List<RawScanArtifactBlock>
            {
                new()
                {
                    Offset = 0,
                    Length = payload.Length,
                    Layout = CreateLayout(0, 2, 2, 4, isLastBlock: true)
                }
            },
            width: 2,
            height: 2);

        var decoder = new RawScanDecoder(new NAPS2.Images.ImageSharp.ImageSharpImageContext());
        using var image = await decoder.DecodeAsync(artifact);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        ImageAsserts.PixelColors(image, new()
        {
            { (0, 0), (10, 10, 10) },
            { (1, 0), (20, 20, 20) },
            { (0, 1), (30, 30, 30) },
            { (1, 1), (40, 40, 40) }
        });
    }

    [Fact]
    public async Task DecodesMultipleStripsAndContinuationBlocks()
    {
        var payload = new byte[] { 10, 20, 30, 40 };
        var artifact = CreateArtifact(
            "raster-strips.raw",
            payload,
            new List<RawScanArtifactBlock>
            {
                new()
                {
                    Offset = 0,
                    Length = 1,
                    Layout = CreateLayout(0, 2, 1, 2, isLastBlock: false)
                },
                new()
                {
                    Offset = 1,
                    Length = 1,
                    Layout = null
                },
                new()
                {
                    Offset = 2,
                    Length = 2,
                    Layout = CreateLayout(0, 2, 1, 2, isLastBlock: true)
                }
            },
            width: 2,
            height: 2);

        var decoder = new RawScanDecoder(new NAPS2.Images.ImageSharp.ImageSharpImageContext());
        using var image = await decoder.DecodeAsync(artifact);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        ImageAsserts.PixelColors(image, new()
        {
            { (0, 0), (10, 10, 10) },
            { (1, 0), (20, 20, 20) },
            { (0, 1), (30, 30, 30) },
            { (1, 1), (40, 40, 40) }
        });
    }

    [Fact]
    public async Task EnumeratesAndDecodesPageBoundaries()
    {
        var payload = new byte[] { 10, 20, 30, 40 };
        var artifact = CreateArtifact(
            "raster-pages.raw",
            payload,
            new List<RawScanArtifactBlock>
            {
                new()
                {
                    Offset = 0,
                    Length = 2,
                    Layout = CreateLayout(0, 2, 1, 2, isLastBlock: false)
                },
                new()
                {
                    Offset = 2,
                    Length = 2,
                    Layout = CreateLayout(1, 2, 1, 2, isLastBlock: true)
                }
            },
            width: null,
            height: null,
            pageCount: 2);

        var decoder = new RawScanDecoder(new NAPS2.Images.ImageSharp.ImageSharpImageContext());
        var pages = await decoder.EnumeratePagesAsync(artifact);

        Assert.Collection(pages,
            page =>
            {
                Assert.Equal(0, page.PageIndex);
                Assert.Equal(2, page.Width);
                Assert.Equal(1, page.Height);
                Assert.Equal(0L, page.Offset);
                Assert.Equal(2L, page.Length);
            },
            page =>
            {
                Assert.Equal(1, page.PageIndex);
                Assert.Equal(2, page.Width);
                Assert.Equal(1, page.Height);
                Assert.Equal(2L, page.Offset);
                Assert.Equal(2L, page.Length);
            });

        using var image = await decoder.DecodeAsync(artifact, pageIndex: 1);
        ImageAsserts.PixelColors(image, new()
        {
            { (0, 0), (30, 30, 30) },
            { (1, 0), (40, 40, 40) }
        });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => decoder.DecodeAsync(artifact, pageIndex: 2));
    }

    [Fact]
    public async Task RejectsIncompleteStripLayout()
    {
        var payload = new byte[] { 10 };
        var artifact = CreateArtifact(
            "raster-incomplete.raw",
            payload,
            new List<RawScanArtifactBlock>
            {
                new()
                {
                    Offset = 0,
                    Length = payload.Length,
                    Layout = CreateLayout(0, 2, 1, 2, isLastBlock: true)
                }
            },
            width: 2,
            height: 1);

        var decoder = new RawScanDecoder(ImageContext);

        await Assert.ThrowsAsync<InvalidDataException>(() => decoder.DecodeAsync(artifact));
    }

    [Fact]
    public async Task RejectsMissingFinalBlockAndNonContiguousOffsets()
    {
        var payload = new byte[] { 10, 20 };
        var missingFinal = CreateArtifact(
            "raster-no-final.raw",
            payload,
            new List<RawScanArtifactBlock>
            {
                new()
                {
                    Offset = 0,
                    Length = 2,
                    Layout = CreateLayout(0, 2, 1, 2, isLastBlock: false)
                }
            },
            width: 2,
            height: 1);
        var nonContiguous = CreateArtifact(
            "raster-gap.raw",
            payload,
            new List<RawScanArtifactBlock>
            {
                new()
                {
                    Offset = 1,
                    Length = 2,
                    Layout = CreateLayout(0, 2, 1, 2, isLastBlock: true)
                }
            },
            width: 2,
            height: 1);
        var decoder = new RawScanDecoder(ImageContext);

        await Assert.ThrowsAsync<InvalidDataException>(() => decoder.DecodeAsync(missingFinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => decoder.DecodeAsync(nonContiguous));
    }

    private RawScanArtifactDescriptor CreateArtifact(
        string fileName,
        byte[] payload,
        List<RawScanArtifactBlock> blocks,
        int? width,
        int? height,
        int pageCount = 1)
    {
        var path = Path.Combine(FolderPath, fileName);
        File.WriteAllBytes(path, payload);
        return new RawScanArtifactDescriptor(
            path,
            new RawScanArtifactHeader
            {
                Type = RawScanArtifactType.RasterBlock,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = width,
                Height = height,
                PageCount = pageCount
            },
            new RawScanArtifactMetadata
            {
                ByteLength = payload.Length,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = width,
                Height = height,
                PageCount = pageCount
            })
        {
            ByteLength = payload.Length,
            IsCommitted = true,
            Blocks = blocks
        };
    }

    private static RawBlockLayout CreateLayout(int pageIndex, int width, int height, int stride,
        bool isLastBlock)
    {
        return new RawBlockLayout
        {
            Offset = pageIndex == 0 ? null : 2,
            Width = width,
            Height = height,
            Stride = stride,
            BitsPerPixel = 8,
            BytesPerPixel = 1,
            PixelFormat = ImagePixelFormat.Gray8,
            SubPixelType = SubPixelType.Gray,
            FrameType = RawScanFrameType.Gray,
            PageIndex = pageIndex,
            IsLastBlock = isLastBlock
        };
    }
}
