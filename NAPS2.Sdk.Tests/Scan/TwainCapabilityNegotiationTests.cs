#if !MACOS
using NAPS2.Scan;
using NAPS2.Scan.Exceptions;
using NAPS2.Scan.Internal.Twain;
using NSubstitute;
using NTwain;
using NTwain.Data;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class TwainCapabilityNegotiationTests
{
    [Fact]
    public void RightAlignmentDoesNotQueryPhysicalWidth()
    {
        Assert.Equal(0, TwainScanRunner.GetHorizontalOffset(HorizontalAlign.Right, 8.5f,
            () => throw new InvalidOperationException("Malformed width capability")));
    }

    [Theory]
    [InlineData(HorizontalAlign.Center, 0.5f)]
    [InlineData(HorizontalAlign.Left, 1f)]
    public void AlignmentUsesPhysicalWidth(HorizontalAlign align, float expected)
    {
        Assert.Equal(expected, TwainScanRunner.GetHorizontalOffset(align, 8.5f, () => (TWFix32)9.5));
    }

    [Fact]
    public void UnavailableWidthDoesNotInventAnAlignment()
    {
        Assert.Throws<DeviceException>(() =>
            TwainScanRunner.GetHorizontalOffset(HorizontalAlign.Center, 8.5f, () => (TWFix32)0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnavailableResolutionListStillTriesRequestedValue(bool canGet)
    {
        var cap = Substitute.For<ICapWrapper<TWFix32>>();
        cap.CanGet.Returns(canGet);
        cap.GetValues().Returns(Array.Empty<TWFix32>());
        cap.SetValue((TWFix32)300).Returns(ReturnCode.Success);

        TwainScanRunner.SetClosest(cap, 300);

        cap.Received(1).SetValue((TWFix32)300);
        if (!canGet) cap.DidNotReceive().GetValues();
    }

    [Fact]
    public void SupportedResolutionListChoosesClosestValue()
    {
        var cap = Substitute.For<ICapWrapper<TWFix32>>();
        cap.CanGet.Returns(true);
        cap.GetValues().Returns(new TWFix32[] { 200, 300, 600 });
        cap.SetValue((TWFix32)300).Returns(ReturnCode.Success);

        TwainScanRunner.SetClosest(cap, 320);

        cap.Received(1).SetValue((TWFix32)300);
    }

    [Fact]
    public void FailedResolutionWriteReportsCapabilityAndValue()
    {
        var cap = Substitute.For<ICapWrapper<TWFix32>>();
        cap.Capability.Returns(CapabilityId.ICapXResolution);
        cap.SetValue((TWFix32)300).Returns(ReturnCode.Failure);

        var error = Assert.Throws<DeviceException>(() => TwainScanRunner.SetClosest(cap, 300));

        Assert.Contains("ICapXResolution", error.Message);
        Assert.Contains("300", error.Message);
        Assert.Contains("Failure", error.Message);
    }
}
#endif
