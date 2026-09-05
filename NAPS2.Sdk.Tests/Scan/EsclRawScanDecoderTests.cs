using NAPS2.Scan;
using NAPS2.Sdk.Tests.Asserts;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class EsclRawScanDecoderTests : ContextualTests
{
    [Fact]
    public async Task EnumeratePdfReturnsAllPagesWithoutRenderingThem()
    {
        var path = CopyResourceToFile(PdfResources.word_generated_pdf, "raw.pdf");
        var decoder = new EsclRawScanDecoder(ImageContext);

        var pages = await decoder.EnumeratePagesAsync(CreatePdfArtifact(path));

        Assert.Equal(2, pages.Count);
        Assert.Equal(0, pages[0].PageIndex);
        Assert.Equal(1, pages[1].PageIndex);
        Assert.Equal(300, pages[0].HorizontalResolution);
    }

    [Fact]
    public async Task DecodePdfRendersOnlyRequestedPage()
    {
        var path = CopyResourceToFile(PdfResources.word_generated_pdf, "raw.pdf");
        var decoder = new EsclRawScanDecoder(ImageContext);

        using var image = await decoder.DecodeAsync(CreatePdfArtifact(path), pageIndex: 1);

        ImageAsserts.Similar(PdfResources.word_p2, image, ignoreResolution: true);
    }

    [Fact]
    public async Task DecodePdfRejectsPageAfterContainerEnd()
    {
        var path = CopyResourceToFile(PdfResources.word_generated_pdf, "raw.pdf");
        var decoder = new EsclRawScanDecoder(ImageContext);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            decoder.DecodeAsync(CreatePdfArtifact(path), pageIndex: 2));
    }

    private static RawScanArtifactDescriptor CreatePdfArtifact(string path) => new()
    {
        PayloadPath = path,
        Header = new RawScanArtifactHeader
        {
            Type = RawScanArtifactType.EncodedContainer,
            ContentType = "application/pdf",
            FileExtension = ".pdf",
            HorizontalResolution = 300,
            VerticalResolution = 300
        },
        Metadata = new RawScanArtifactMetadata
        {
            ContentType = "application/pdf",
            HorizontalResolution = 300,
            VerticalResolution = 300,
            PageCount = null
        }
    };
}
