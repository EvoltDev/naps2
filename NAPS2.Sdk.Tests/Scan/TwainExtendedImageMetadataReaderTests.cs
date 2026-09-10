#if !MACOS
using System.Text;
using NAPS2.Scan;
using NAPS2.Scan.Internal.Twain;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class TwainExtendedImageMetadataReaderTests
{
    [Fact]
    public void Parse_AlignsBarcodeTextAndTypeByIndex()
    {
        var result = TwainExtendedImageMetadataReader.Parse(new TwainExtendedImageValues
        {
            PageSide = 2,
            BarcodeCount = [2],
            BarcodeTypes = [20, 3],
            BarcodeTextLengths = [7, 6],
            BarcodeText = Encoding.ASCII.GetBytes("QR-1234ABC-93"),
            BarcodeX = [10, 20],
            BarcodeY = [30, 40],
            BarcodeConfidence = [95, 80],
            BarcodeRotation = [0, 1]
        });

        Assert.Equal(RawScanPageSide.Back, result.PageSide);
        Assert.Equal("2", result.AdditionalMetadata["twain.barcode.count"]);
        Assert.Equal("QR-1234", result.AdditionalMetadata["twain.barcode.0.text"]);
        Assert.Equal("20", result.AdditionalMetadata["twain.barcode.0.typeCode"]);
        Assert.Equal("QRCode", result.AdditionalMetadata["twain.barcode.0.type"]);
        Assert.Equal("ABC-93", result.AdditionalMetadata["twain.barcode.1.text"]);
        Assert.Equal("3", result.AdditionalMetadata["twain.barcode.1.typeCode"]);
        Assert.Equal("Code93", result.AdditionalMetadata["twain.barcode.1.type"]);
        Assert.Equal("20", result.AdditionalMetadata["twain.barcode.1.x"]);
        Assert.Equal("40", result.AdditionalMetadata["twain.barcode.1.y"]);
        Assert.Equal("80", result.AdditionalMetadata["twain.barcode.1.confidence"]);
        Assert.Equal("1", result.AdditionalMetadata["twain.barcode.1.rotation"]);
    }

    [Fact]
    public void Parse_PreservesTypesWhenBarcodeTextIsIncomplete()
    {
        var result = TwainExtendedImageMetadataReader.Parse(new TwainExtendedImageValues
        {
            BarcodeCount = [2],
            BarcodeTypes = [4, 20],
            BarcodeTextLengths = [3, 4],
            BarcodeText = Encoding.ASCII.GetBytes("ABC")
        });

        Assert.Equal("ABC", result.AdditionalMetadata["twain.barcode.0.text"]);
        Assert.False(result.AdditionalMetadata.ContainsKey("twain.barcode.1.text"));
        Assert.Equal("Code128", result.AdditionalMetadata["twain.barcode.0.type"]);
        Assert.Equal("QRCode", result.AdditionalMetadata["twain.barcode.1.type"]);
    }

    [Fact]
    public void Parse_RecordsDisabledBarcodeEngineWithoutCreatingDetectedCodes()
    {
        var result = TwainExtendedImageMetadataReader.Parse(new TwainExtendedImageValues
        {
            BarcodeCount = [uint.MaxValue]
        });

        Assert.Equal("-1", result.AdditionalMetadata["twain.barcode.count"]);
        Assert.Single(result.AdditionalMetadata);
    }
}
#endif
