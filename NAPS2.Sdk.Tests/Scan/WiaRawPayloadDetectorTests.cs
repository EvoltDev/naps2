using NAPS2.Images;
using NAPS2.Scan;
using NAPS2.Scan.Internal.Wia;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class WiaRawPayloadDetectorTests
{
    [Fact]
    public void DetectsEncodedImageFromSignatureAndRestoresStreamPosition()
    {
        using var stream = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        stream.Position = 2;

        var payload = WiaRawPayloadDetector.Detect(stream);

        Assert.Equal(2, stream.Position);
        Assert.Equal(RawScanArtifactType.EncodedImage, payload.Type);
        Assert.Equal(ImageFileFormat.Png, payload.ImageFormat);
        Assert.Equal("image/png", payload.ContentType);
        Assert.Equal(".png", payload.FileExtension);
        Assert.False(payload.IsContainer);
    }

    [Fact]
    public void DetectsTiffAsAnEncodedContainer()
    {
        using var stream = new MemoryStream([0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0]);

        var payload = WiaRawPayloadDetector.Detect(stream);

        Assert.Equal(RawScanArtifactType.EncodedContainer, payload.Type);
        Assert.Equal(ImageFileFormat.Tiff, payload.ImageFormat);
        Assert.Equal("image/tiff", payload.ContentType);
        Assert.Equal(".tiff", payload.FileExtension);
        Assert.True(payload.IsContainer);
    }

    [Fact]
    public void UsesPdfSignatureAndIgnoresUnknownExtension()
    {
        using var stream = new MemoryStream("%PDF-1.7"u8.ToArray());

        var payload = WiaRawPayloadDetector.Detect(stream, "scan.dat");

        Assert.Equal(RawScanArtifactType.EncodedContainer, payload.Type);
        Assert.Equal(ImageFileFormat.Unknown, payload.ImageFormat);
        Assert.Equal("application/pdf", payload.ContentType);
        Assert.Equal(".pdf", payload.FileExtension);
        Assert.True(payload.IsContainer);
    }
}
