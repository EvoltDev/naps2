using NAPS2.Scan;
using NAPS2.Scan.Internal;
using NAPS2.Scan.Internal.Wia;
using Xunit;
using static NAPS2.Sdk.Tests.Scan.KeyedSettingNegotiatorTests;

namespace NAPS2.Sdk.Tests.Scan;

public class WiaSettingBindingsTests
{
    [Theory]
    [InlineData("low", 1)]
    [InlineData("medium", 51)]
    [InlineData("high", 100)]
    public void MultifeedSensitivityLevelsArePlacedOnTheDriversRange(string level, int expected)
    {
        var access = new FakeAccess { Ranges = { ["WIA_IPS_MULTI_FEED_SENSITIVITY"] = (1, 100) } };

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.MultifeedSensitivity, DriverSettingValue.FromText(level)))));

        Assert.Equal(expected, access.Values["WIA_IPS_MULTI_FEED_SENSITIVITY"]);
        Assert.Equal(DriverProcessingStatus.Applied, result.Status);
    }

    [Fact]
    public void MultifeedSensitivityLevelsArePlacedOnAListedScale()
    {
        var access = new FakeAccess { Offered = { ["WIA_IPS_MULTI_FEED_SENSITIVITY"] = [30, 10, 20] } };

        Apply(access, Options((DriverSettingKeys.MultifeedSensitivity, DriverSettingValue.FromText("medium"))));

        Assert.Equal(20, access.Values["WIA_IPS_MULTI_FEED_SENSITIVITY"]);
    }

    [Fact]
    public void AMissingSensitivityPropertyIsUnsupportedNotMalformed()
    {
        var access = new FakeAccess
        {
            States = { ["WIA_IPS_MULTI_FEED_SENSITIVITY"] = DriverProcessingCapabilityState.Unsupported }
        };

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.MultifeedSensitivity, DriverSettingValue.FromText("high")))));

        Assert.Equal(DriverProcessingStatus.Unsupported, result.Status);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void AnRgbDropoutSelectsTheRgbModeBeforeEachComponent()
    {
        var access = new FakeAccess();

        var results = Apply(access, Options(
            (DriverSettingKeys.ColorDropoutRed, DriverSettingValue.FromInteger(200)),
            (DriverSettingKeys.ColorDropoutGreen, DriverSettingValue.FromInteger(30)),
            (DriverSettingKeys.ColorDropoutBlue, DriverSettingValue.FromInteger(40))));

        Assert.Equal("WIA_IPS_COLOR_DROP", access.Writes[0]);
        Assert.Equal(4, access.Values["WIA_IPS_COLOR_DROP"]);
        Assert.Equal(200, access.Values["WIA_IPS_COLOR_DROP_RED"]);
        Assert.Equal(30, access.Values["WIA_IPS_COLOR_DROP_GREEN"]);
        Assert.Equal(40, access.Values["WIA_IPS_COLOR_DROP_BLUE"]);
        Assert.All(results, x => Assert.Equal(DriverProcessingStatus.Applied, x.Status));
    }

    [Fact]
    public void BarcodeTypesAreWrittenWithTheWiaIdentifiers()
    {
        var access = new FakeAccess { Offered = { ["WIA_IPS_ENABLED_BARCODE_TYPES"] = [9, 28, 41] } };

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.BarcodeTypes, DriverSettingValue.FromText("qr,code128")))));

        Assert.Equal(new object[] { 41, 9 }, (object[]) access.Values["WIA_IPS_ENABLED_BARCODE_TYPES"]);
        Assert.Equal(DriverProcessingStatus.Applied, result.Status);
        Assert.Equal("qr,code128", result.EffectiveValue);
    }

    [Fact]
    public void BarcodeTypesTheDriverDoesNotSupportAreRejectedWithoutWriting()
    {
        var access = new FakeAccess { Offered = { ["WIA_IPS_ENABLED_BARCODE_TYPES"] = [9] } };

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.BarcodeTypes, DriverSettingValue.FromText("qr")))));

        Assert.Equal(DriverProcessingStatus.Rejected, result.Status);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void PostnetHasNoSingleWiaEquivalent()
    {
        var result = Assert.Single(Apply(new FakeAccess(),
            Options((DriverSettingKeys.BarcodeTypes, DriverSettingValue.FromText("postnet")))));

        Assert.Equal(DriverProcessingStatus.Rejected, result.Status);
    }

    private static List<DriverProcessingSetting> Apply(FakeAccess access, DriverProcessingOptions options) =>
        KeyedSettingNegotiator.Apply(access, "WIA",
                KeyedSettingNegotiator.Candidates(WiaSettingBindings.Bindings, [], null), WiaSettingBindings.Gaps,
                options)
            .ToList();
}
