#if MACOS
using NAPS2.Images;
using NAPS2.Scan;
using NAPS2.Sdk.Tests.Asserts;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class AppleRawScanDecoderTests : ContextualTests
{
    [Fact]
    public async Task DecodesNativeGrayRowsWithoutImageConstructionDuringAcquisition()
    {
        var path = Path.Combine(FolderPath, "apple-gray.raw");
        File.WriteAllBytes(path, new byte[] { 10, 20, 0xEE, 0xEE, 30, 40, 0xDD, 0xDD });
        var artifact = new RawScanArtifactDescriptor(path,
            new RawScanArtifactHeader
            {
                Type = RawScanArtifactType.AppleBand,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = 2,
                Height = 2,
                Stride = 4,
                HorizontalResolution = 300,
                VerticalResolution = 300
            },
            new RawScanArtifactMetadata
            {
                ByteLength = 8,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = 2,
                Height = 2,
                HorizontalResolution = 300,
                VerticalResolution = 300,
                FrameCount = 1,
                AdditionalMetadata = new Dictionary<string, string?>
                {
                    ["apple.bytes-per-row"] = "4"
                }
            });

        var decoder = new AppleRawScanDecoder(new NAPS2.Images.ImageSharp.ImageSharpImageContext());
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
    public async Task RejectsPayloadShorterThanDeclaredLayout()
    {
        var path = Path.Combine(FolderPath, "apple-short.raw");
        File.WriteAllBytes(path, new byte[] { 10, 20, 0xEE, 0xEE, 30, 40, 0xDD });
        var artifact = new RawScanArtifactDescriptor(path,
            new RawScanArtifactHeader
            {
                Type = RawScanArtifactType.AppleBand,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = 2,
                Height = 2,
                Stride = 4
            },
            new RawScanArtifactMetadata
            {
                ByteLength = 7,
                PixelFormat = ImagePixelFormat.Gray8,
                SubPixelType = SubPixelType.Gray,
                FrameType = RawScanFrameType.Gray,
                Width = 2,
                Height = 2,
                AdditionalMetadata = new Dictionary<string, string?>
                {
                    ["apple.bytes-per-row"] = "4"
                }
            });

        var decoder = new AppleRawScanDecoder(new NAPS2.Images.ImageSharp.ImageSharpImageContext());

        await Assert.ThrowsAsync<InvalidDataException>(() => decoder.DecodeAsync(artifact));
    }
}
#endif
