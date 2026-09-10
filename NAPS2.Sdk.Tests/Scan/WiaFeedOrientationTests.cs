using NAPS2.Images;
using NAPS2.Scan;
using NAPS2.Scan.Internal.Wia;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class WiaFeedOrientationTests
{
    [Theory]
    [InlineData(null, 8000, 11000)]
    [InlineData(WiaFeedOrientation.Portrait, 8000, 11000)]
    [InlineData(WiaFeedOrientation.Landscape, 11000, 8000)]
    public void CustomPageDimensionsFollowPhysicalFeed(WiaFeedOrientation? orientation, int width, int height)
    {
        var dimensions = WiaFeedOrientationConfiguration.GetPageDimensions(new PageSize(8, 11, PageSizeUnit.Inch), orientation);
        Assert.Equal((width, height), dimensions);
    }

    [Fact]
    public void LandscapeDimensionsAreNotSwappedTwice()
    {
        var dimensions = WiaFeedOrientationConfiguration.GetPageDimensions(new PageSize(11, 8, PageSizeUnit.Inch), WiaFeedOrientation.Landscape);
        Assert.Equal((11000, 8000), dimensions);
    }

    [Fact]
    public void DriverDefaultDoesNotAccessOrientationProperty()
    {
        WiaFeedOrientationConfiguration.Apply(null, _ => throw new Exception(), () => throw new Exception());
        WiaFeedOrientationConfiguration.Verify(null, () => throw new Exception());
    }

    [Fact]
    public void ExplicitOrientationIsWrittenAndVerified()
    {
        var value = 0;
        WiaFeedOrientationConfiguration.Apply(WiaFeedOrientation.Landscape, requested => value = requested, () => value);
        Assert.Equal(1, value);
    }

    [Fact]
    public void MissingPropertyDoesNotPretendTheRequestedOrientationWasApplied()
    {
        Assert.Throws<InvalidOperationException>(() => WiaFeedOrientationConfiguration.Apply(
            WiaFeedOrientation.Landscape, _ => throw new Exception("Unexpected write"), () => null));
    }

    [Fact]
    public void RejectedOrientationAndSubsequentResetAreDetected()
    {
        Assert.Throws<InvalidOperationException>(() => WiaFeedOrientationConfiguration.Apply(
            WiaFeedOrientation.Landscape, _ => { }, () => 0));
        var value = 0;
        WiaFeedOrientationConfiguration.Apply(WiaFeedOrientation.Landscape, requested => value = requested, () => value);
        value = 0;
        Assert.Throws<InvalidOperationException>(() => WiaFeedOrientationConfiguration.Verify(WiaFeedOrientation.Landscape, () => value));
    }
}
