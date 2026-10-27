#if !MACOS
using NAPS2.Scan;
using NAPS2.Scan.Internal.Twain;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class TwainDriverProcessingTests
{
    [Theory]
    [InlineData(true, TwainTransferMode.Memory, true)]
    [InlineData(true, TwainTransferMode.Native, false)]
    [InlineData(false, TwainTransferMode.Memory, false)]
    [InlineData(false, TwainTransferMode.Native, false)]
    public void OnlyARawMemoryTransferDecodesCompression(bool raw, TwainTransferMode mode, bool expected)
    {
        Assert.Equal(expected, TwainScanRunner.DecodesCompressedTransfers(raw, mode));
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("group4")]
    public void AnUndecodableCompressionIsRejectedBeforeItReachesTheSource(string compression)
    {
        var gamma = Request(DriverSettingKeys.Gamma, DriverSettingValue.FromReal(2.2));
        var quality = Request(DriverSettingKeys.TransferJpegQuality, DriverSettingValue.FromInteger(80));
        var keyed = new List<DriverSettingRequest>
        {
            gamma, Request(DriverSettingKeys.TransferCompression, DriverSettingValue.FromText(compression)), quality
        };
        var requested = new Dictionary<string, object?>();
        var settings = new List<DriverProcessingSetting>();
        var rejected = new List<string>();

        var remaining = TwainDriverProcessing.WithoutUndecodableCompression(keyed, requested, settings, rejected);

        Assert.Equal([gamma, quality], remaining);
        var setting = Assert.Single(settings);
        Assert.Equal(DriverSettingKeys.TransferCompression, setting.Name);
        Assert.Equal(DriverProcessingStatus.Rejected, setting.Status);
        Assert.Equal(compression, setting.RequestedValue);
        Assert.Equal([DriverSettingKeys.TransferCompression], rejected);
        Assert.Equal(compression, requested[DriverSettingKeys.TransferCompression]);
    }

    [Fact]
    public void AnUncompressedTransferIsAlwaysNegotiated()
    {
        var keyed = new List<DriverSettingRequest>
        {
            Request(DriverSettingKeys.TransferCompression, DriverSettingValue.FromText("none"))
        };
        var settings = new List<DriverProcessingSetting>();

        var remaining = TwainDriverProcessing.WithoutUndecodableCompression(keyed,
            new Dictionary<string, object?>(), settings, new List<string>());

        Assert.Same(keyed, remaining);
        Assert.Empty(settings);
    }

    [Fact]
    public void ADryRunThatCannotSelectTheTransferMechanismDoesNotSucceed()
    {
        var accepted = new DriverProcessingResult
        {
            Settings =
            [
                new DriverProcessingSetting
                {
                    Name = DriverSettingKeys.TransferCompression,
                    Status = DriverProcessingStatus.Applied,
                    RequestedValue = "none"
                }
            ]
        };

        var result = LocalTwainController.WithTransferFailure(accepted,
            "The TWAIN source does not support raw memory transfer mode.");

        Assert.False(result.Succeeded);
        Assert.Contains("raw memory transfer mode", result.FailureReason);
        Assert.Single(result.Settings);
        Assert.Same(accepted, LocalTwainController.WithTransferFailure(accepted, null));
    }

    [Fact]
    public void ADuplicateRequestCannotStandInForARejectedCompression()
    {
        var keyed = new List<DriverSettingRequest>
        {
            Request(DriverSettingKeys.TransferCompression, DriverSettingValue.FromText("jpeg")),
            Request(DriverSettingKeys.TransferCompression, DriverSettingValue.FromText("group4"))
        };
        var settings = new List<DriverProcessingSetting>();

        var remaining = TwainDriverProcessing.WithoutUndecodableCompression(keyed,
            new Dictionary<string, object?>(), settings, new List<string>());

        Assert.Empty(remaining);
        Assert.Equal(["jpeg", "group4"], settings.Select(x => x.RequestedValue));
        Assert.All(settings, x => Assert.Equal(DriverProcessingStatus.Rejected, x.Status));
        Assert.Contains("more than once", settings[1].Message);
    }

    private static DriverSettingRequest Request(string key, DriverSettingValue value) =>
        new() { Key = key, Value = value };

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
