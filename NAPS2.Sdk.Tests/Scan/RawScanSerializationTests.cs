using NAPS2.Images;
using NAPS2.Scan;
using NAPS2.Serialization;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class RawScanSerializationTests
{
    public static TheoryData<SubPixelType> PixelTypes => new()
    {
        SubPixelType.Rgba, SubPixelType.Rgb, SubPixelType.Rgbn, SubPixelType.Bgra,
        SubPixelType.Bgr, SubPixelType.Gray, SubPixelType.Bit, SubPixelType.InvertedBit
    };

    [Theory]
    [MemberData(nameof(PixelTypes))]
    public void PixelLayoutRoundTripsJsonWithValueEquality(SubPixelType original)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var copy = System.Text.Json.JsonSerializer.Deserialize<SubPixelType>(json);

        Assert.NotSame(original, copy);
        Assert.True(original == copy);
        Assert.Equal(original.GetHashCode(), copy!.GetHashCode());
        foreach (var other in PixelTypes)
            Assert.Equal(original == (SubPixelType)other[0], copy == (SubPixelType)other[0]);
    }

    [Fact]
    public void ArtifactMetadataRoundTripsAdditionalMetadataDictionary()
    {
        var original = new RawScanArtifactMetadata
        {
            ByteLength = 128,
            ContentType = "application/octet-stream",
            AdditionalMetadata = new Dictionary<string, string?>
            {
                ["driver"] = "twain",
                ["optional"] = null
            }
        };

        var serializer = new XmlSerializer<RawScanArtifactMetadata>();
        var document = serializer.SerializeToXDocument(original);

        Assert.NotNull(document.Root?.Element(nameof(RawScanArtifactMetadata.AdditionalMetadata)));

        var copy = serializer.DeserializeFromXDocument(document);

        Assert.NotNull(copy);
        Assert.Equal(original.ByteLength, copy.ByteLength);
        Assert.Equal(original.ContentType, copy.ContentType);
        Assert.Equal("twain", copy.AdditionalMetadata!["driver"]);
        Assert.Null(copy.AdditionalMetadata["optional"]);
    }

    [Fact]
    public void ArtifactDescriptorRoundTripsPageMetadataList()
    {
        var original = new RawScanArtifactDescriptor
        {
            PayloadPath = "/tmp/scan.tiff",
            IsCommitted = true,
            Blocks =
            [
                new RawScanArtifactBlock
                {
                    Offset = 0,
                    Length = 4096,
                    Layout = new RawBlockLayout
                    {
                        Offset = 0,
                        Width = 1200,
                        Height = 1800,
                        Stride = 3600,
                        BitsPerPixel = 24,
                        BytesPerPixel = 3,
                        PixelFormat = ImagePixelFormat.RGB24,
                        PageIndex = 0,
                        IsLastBlock = true
                    }
                }
            ],
            Pages =
            [
                new RawScanPageMetadata
                {
                    PageIndex = 0,
                    Side = RawScanPageSide.Front,
                    Width = 1200,
                    Height = 1800,
                    HorizontalResolution = 300,
                    VerticalResolution = 300,
                    PixelFormat = ImagePixelFormat.RGB24,
                    Offset = 0,
                    Length = 4096,
                    SourceId = "front"
                },
                new RawScanPageMetadata
                {
                    PageIndex = 1,
                    Side = RawScanPageSide.Back,
                    Width = 1200,
                    Height = 1800,
                    HorizontalResolution = 300,
                    VerticalResolution = 300,
                    PixelFormat = ImagePixelFormat.RGB24,
                    Offset = 4096,
                    Length = 4096,
                    SourceId = "back"
                }
            ]
        };

        var serializer = new XmlSerializer<RawScanArtifactDescriptor>();
        var document = serializer.SerializeToXDocument(original);
        var copy = serializer.DeserializeFromXDocument(document);

        Assert.NotNull(copy);
        Assert.Equal(original.PayloadPath, copy.PayloadPath);
        Assert.True(copy.IsCommitted);
        Assert.Collection(copy.Blocks,
            block =>
            {
                Assert.Equal(0, block.Offset);
                Assert.Equal(4096, block.Length);
                Assert.Equal(0L, block.Layout?.Offset);
                Assert.Equal(1200, block.Layout?.Width);
                Assert.Equal(ImagePixelFormat.RGB24, block.Layout?.PixelFormat);
                Assert.True(block.Layout?.IsLastBlock == true);
            });
        Assert.Collection(copy.Pages,
            page =>
            {
                Assert.Equal(0, page.PageIndex);
                Assert.Equal(RawScanPageSide.Front, page.Side);
                Assert.Equal(4096, page.Length);
                Assert.Equal("front", page.SourceId);
            },
            page =>
            {
                Assert.Equal(1, page.PageIndex);
                Assert.Equal(RawScanPageSide.Back, page.Side);
                Assert.Equal(4096, page.Length);
                Assert.Equal("back", page.SourceId);
            });
    }

    [Fact]
    public void ProcessingResultRoundTripsRequestedAppliedAndRejectedSettings()
    {
        var original = new DriverProcessingResult
        {
            RequestedSettings = new Dictionary<string, object?>
            {
                ["deskew"] = true,
                ["mode"] = "color"
            },
            EffectiveSettings = new Dictionary<string, object?>
            {
                ["deskew"] = true,
                ["mode"] = "gray"
            },
            Settings =
            [
                new DriverProcessingSetting
                {
                    Name = "deskew",
                    Status = DriverProcessingStatus.Applied,
                    RequestedValue = true,
                    EffectiveValue = true
                },
                new DriverProcessingSetting
                {
                    Name = "mode",
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = "color",
                    EffectiveValue = "gray",
                    Message = "The requested mode is unavailable."
                }
            ],
            RejectedSettings = ["mode"]
        };

        var serializer = new XmlSerializer<DriverProcessingResult>();
        var document = serializer.SerializeToXDocument(original);

        Assert.NotNull(document.Root?.Element(nameof(DriverProcessingResult.RequestedSettingsData)));
        Assert.NotNull(document.Root?.Element(nameof(DriverProcessingResult.SettingsData)));
        Assert.DoesNotContain(document.Root!.Elements(), element =>
            element.Name.LocalName == nameof(DriverProcessingResult.RequestedSettings));

        var copy = serializer.DeserializeFromXDocument(document);

        Assert.NotNull(copy);
        Assert.True((bool) copy.RequestedSettings["deskew"]!);
        Assert.Equal("color", copy.RequestedSettings["mode"]);
        Assert.True((bool) copy.EffectiveSettings["deskew"]!);
        Assert.Equal("gray", copy.EffectiveSettings["mode"]);
        Assert.Collection(copy.Settings,
            setting =>
            {
                Assert.Equal("deskew", setting.Name);
                Assert.Equal(DriverProcessingStatus.Applied, setting.Status);
                Assert.Equal(true, setting.RequestedValue);
                Assert.Equal(true, setting.EffectiveValue);
            },
            setting =>
            {
                Assert.Equal("mode", setting.Name);
                Assert.Equal(DriverProcessingStatus.Rejected, setting.Status);
                Assert.Equal("color", setting.RequestedValue);
                Assert.Equal("gray", setting.EffectiveValue);
                Assert.Equal("The requested mode is unavailable.", setting.Message);
            });
        Assert.Equal(["mode"], copy.RejectedSettings);
        Assert.False(copy.Succeeded);
    }
}
