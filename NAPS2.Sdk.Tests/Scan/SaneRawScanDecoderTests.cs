using NAPS2.Images;
using NAPS2.Scan;
using NAPS2.Sdk.Tests.Asserts;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class SaneRawScanDecoderTests : ContextualTests
{
    [Fact]
    public async Task DecodesInterleavedRowsWithPadding()
    {
        var path = Path.Combine(FolderPath, "sane-gray.raw");
        // Three pixels per row with one byte of SANE row padding.
        File.WriteAllBytes(path, new byte[] { 10, 20, 30, 0xEE, 40, 50, 60, 0xDD });
        var artifact = new RawScanArtifactDescriptor(path,
            new RawScanArtifactHeader
            {
                Type = RawScanArtifactType.SaneFrame,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = 3,
                Height = 2,
                Stride = 4
            },
            new RawScanArtifactMetadata
            {
                ByteLength = 8,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = 3,
                Height = 2,
                FrameCount = 1,
                AdditionalMetadata = new Dictionary<string, string?>
                {
                    ["sane.frame-count"] = "1",
                    ["sane.frame.0.frame"] = "Gray",
                    ["sane.frame.0.offset"] = "0",
                    ["sane.frame.0.length"] = "8",
                    ["sane.frame.0.width"] = "3",
                    ["sane.frame.0.height"] = "2",
                    ["sane.frame.0.stride"] = "4",
                    ["sane.frame.0.depth"] = "8"
                }
            });

        var decoder = new SaneRawScanDecoder(new NAPS2.Images.ImageSharp.ImageSharpImageContext());
        using var image = await decoder.DecodeAsync(artifact);

        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        ImageAsserts.PixelColors(image, new()
        {
            { (0, 0), (10, 10, 10) },
            { (2, 0), (30, 30, 30) },
            { (0, 1), (40, 40, 40) },
            { (2, 1), (60, 60, 60) }
        });
    }

    [Fact]
    public async Task DecodesPlanarRgbAsOneLogicalPage()
    {
        var path = Path.Combine(FolderPath, "sane-planar.raw");
        File.WriteAllBytes(path, new byte[] { 10, 20, 30, 40, 50, 60 });
        var artifact = new RawScanArtifactDescriptor(path,
            new RawScanArtifactHeader
            {
                Type = RawScanArtifactType.SaneFrame,
                PixelFormat = ImagePixelFormat.RGB24,
                SubPixelType = SubPixelType.Rgb,
                FrameType = RawScanFrameType.Image,
                Width = 2,
                Height = 1,
                Stride = 2
            },
            new RawScanArtifactMetadata
            {
                ByteLength = 6,
                PixelFormat = ImagePixelFormat.RGB24,
                SubPixelType = SubPixelType.Rgb,
                FrameType = RawScanFrameType.Image,
                Width = 2,
                Height = 1,
                PageCount = 1,
                FrameCount = 3,
                AdditionalMetadata = new Dictionary<string, string?>
                {
                    ["sane.frame-count"] = "3",
                    ["sane.planar"] = "True",
                    ["sane.frame.0.frame"] = "Red",
                    ["sane.frame.0.offset"] = "0",
                    ["sane.frame.0.length"] = "2",
                    ["sane.frame.0.width"] = "2",
                    ["sane.frame.0.height"] = "1",
                    ["sane.frame.0.stride"] = "2",
                    ["sane.frame.0.depth"] = "8",
                    ["sane.frame.1.frame"] = "Green",
                    ["sane.frame.1.offset"] = "2",
                    ["sane.frame.1.length"] = "2",
                    ["sane.frame.1.width"] = "2",
                    ["sane.frame.1.height"] = "1",
                    ["sane.frame.1.stride"] = "2",
                    ["sane.frame.1.depth"] = "8",
                    ["sane.frame.2.frame"] = "Blue",
                    ["sane.frame.2.offset"] = "4",
                    ["sane.frame.2.length"] = "2",
                    ["sane.frame.2.width"] = "2",
                    ["sane.frame.2.height"] = "1",
                    ["sane.frame.2.stride"] = "2",
                    ["sane.frame.2.depth"] = "8"
                }
            });

        var decoder = new SaneRawScanDecoder(new NAPS2.Images.ImageSharp.ImageSharpImageContext());
        using var image = await decoder.DecodeAsync(artifact);

        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        ImageAsserts.PixelColors(image, new()
        {
            { (0, 0), (10, 30, 50) },
            { (1, 0), (20, 40, 60) }
        });
    }

    [Fact]
    public async Task RejectsPlanarFrameShorterThanDeclaredLayout()
    {
        var path = Path.Combine(FolderPath, "sane-planar-short.raw");
        // The red and green frames are complete; the blue frame is missing one byte. This also exercises cleanup
        // after the destination image has already been allocated and partially populated.
        File.WriteAllBytes(path, new byte[] { 10, 20, 30, 40, 50 });
        var artifact = new RawScanArtifactDescriptor(path,
            new RawScanArtifactHeader
            {
                Type = RawScanArtifactType.SaneFrame,
                PixelFormat = ImagePixelFormat.RGB24,
                SubPixelType = SubPixelType.Rgb,
                FrameType = RawScanFrameType.Image,
                Width = 2,
                Height = 1,
                Stride = 2
            },
            new RawScanArtifactMetadata
            {
                ByteLength = 5,
                PixelFormat = ImagePixelFormat.RGB24,
                SubPixelType = SubPixelType.Rgb,
                FrameType = RawScanFrameType.Image,
                Width = 2,
                Height = 1,
                PageCount = 1,
                FrameCount = 3,
                AdditionalMetadata = new Dictionary<string, string?>
                {
                    ["sane.frame-count"] = "3",
                    ["sane.planar"] = "True",
                    ["sane.frame.0.frame"] = "Red",
                    ["sane.frame.0.offset"] = "0",
                    ["sane.frame.0.length"] = "2",
                    ["sane.frame.0.width"] = "2",
                    ["sane.frame.0.height"] = "1",
                    ["sane.frame.0.stride"] = "2",
                    ["sane.frame.0.depth"] = "8",
                    ["sane.frame.1.frame"] = "Green",
                    ["sane.frame.1.offset"] = "2",
                    ["sane.frame.1.length"] = "2",
                    ["sane.frame.1.width"] = "2",
                    ["sane.frame.1.height"] = "1",
                    ["sane.frame.1.stride"] = "2",
                    ["sane.frame.1.depth"] = "8",
                    ["sane.frame.2.frame"] = "Blue",
                    ["sane.frame.2.offset"] = "4",
                    ["sane.frame.2.length"] = "1",
                    ["sane.frame.2.width"] = "2",
                    ["sane.frame.2.height"] = "1",
                    ["sane.frame.2.stride"] = "2",
                    ["sane.frame.2.depth"] = "8"
                }
            });

        var decoder = new SaneRawScanDecoder(new NAPS2.Images.ImageSharp.ImageSharpImageContext());

        await Assert.ThrowsAsync<InvalidDataException>(() => decoder.DecodeAsync(artifact));
    }

    [Fact]
    public async Task RejectsPayloadShorterThanDeclaredLayout()
    {
        var path = Path.Combine(FolderPath, "sane-short.raw");
        File.WriteAllBytes(path, new byte[] { 10, 20, 30, 0xEE, 40, 50, 60 });
        var artifact = new RawScanArtifactDescriptor(path,
            new RawScanArtifactHeader
            {
                Type = RawScanArtifactType.SaneFrame,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = 3,
                Height = 2,
                Stride = 4
            },
            new RawScanArtifactMetadata
            {
                ByteLength = 7,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = 3,
                Height = 2,
                FrameCount = 1,
                AdditionalMetadata = new Dictionary<string, string?>
                {
                    ["sane.frame-count"] = "1",
                    ["sane.frame.0.frame"] = "Gray",
                    ["sane.frame.0.offset"] = "0",
                    ["sane.frame.0.length"] = "7",
                    ["sane.frame.0.width"] = "3",
                    ["sane.frame.0.height"] = "2",
                    ["sane.frame.0.stride"] = "4",
                    ["sane.frame.0.depth"] = "8"
                }
            });

        var decoder = new SaneRawScanDecoder(ImageContext);

        await Assert.ThrowsAsync<InvalidDataException>(() => decoder.DecodeAsync(artifact));
    }
}
