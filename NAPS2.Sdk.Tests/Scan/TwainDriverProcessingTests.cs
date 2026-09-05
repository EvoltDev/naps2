#if !MACOS
using NAPS2.Scan;
using NAPS2.Scan.Internal.Twain;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class TwainDriverProcessingTests
{
    [Fact]
    public void NativeUiRequestsAreReportedAsNeutralized()
    {
        var options = new DriverProcessingOptions
        {
            Brightness = 150,
            RotationDegrees = 90,
            Deskew = true,
            AutomaticColorDetection = DriverColorDetectionMode.Automatic
        };

        var result = TwainDriverProcessing.NativeUiResult(options);

        Assert.Equal(4, result.Settings.Count);
        Assert.All(result.Settings, setting => Assert.Equal(DriverProcessingStatus.Neutralized, setting.Status));
        Assert.Equal(4, result.NeutralizedSettings.Count);
        Assert.Equal(150, result.RequestedSettings[nameof(DriverProcessingOptions.Brightness)]);
        Assert.Equal(DriverColorDetectionMode.Automatic,
            result.RequestedSettings[nameof(DriverProcessingOptions.AutomaticColorDetection)]);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EmptyNativeUiRequestProducesEmptyResult()
    {
        var result = TwainDriverProcessing.NativeUiResult(new DriverProcessingOptions());

        Assert.Empty(result.Settings);
        Assert.Empty(result.RequestedSettings);
        Assert.Empty(result.NeutralizedSettings);
        Assert.True(result.Succeeded);
    }
}
#endif
