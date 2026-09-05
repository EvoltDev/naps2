using NAPS2.Images;
using Xunit;

namespace NAPS2.Sdk.Tests.Images;

public class ImageAnalysisTests : ContextualTests
{
    [Fact]
    public void AnalyzeBlankPageMatchesExistingBlankDetection()
    {
        using var image = LoadImage(ImageResources.blank1);

        var result = ImageAnalysis.AnalyzeBlankPage(image, 70, 15);

        Assert.True(result.IsBlank);
        Assert.InRange(result.Coverage, 0, 0.01);
    }

    [Fact]
    public void GetSkewAngleMatchesExistingDeskewAnalysis()
    {
        using var image = LoadImage(ImageResources.skewed);

        var angle = ImageAnalysis.GetSkewAngle(image);

        Assert.InRange(angle, 15.5, 16.5);
    }
}
