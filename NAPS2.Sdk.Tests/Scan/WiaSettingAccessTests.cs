#if !MACOS
using NAPS2.Scan;
using NAPS2.Scan.Internal;
using NAPS2.Scan.Internal.Wia;
using NAPS2.Wia;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class WiaSettingAccessTests
{
    private static readonly DriverProcessingOptions LongDocument = new()
    {
        Settings =
        [
            new DriverSettingRequest
            {
                Key = DriverSettingKeys.LongDocument, Value = DriverSettingValue.FromBoolean(true)
            }
        ]
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<NativeSettingBinding>> Candidates =
        KeyedSettingNegotiator.Candidates(WiaSettingBindings.Bindings, [], null);

    [Fact]
    public void AnAbsentPropertyIsUnsupported()
    {
        var access = new WiaSettingAccess(_ => null);

        var result = Assert.Single(KeyedSettingNegotiator.Apply(access, "WIA", Candidates,
            WiaSettingBindings.Gaps, LongDocument));

        Assert.Equal(DriverProcessingStatus.Unsupported, result.Status);
    }

    [Fact]
    public void AFailedPropertyLookupIsAFailureNotUnsupported()
    {
        var access = new WiaSettingAccess(_ => throw new InvalidOperationException("RPC server unavailable"));
        var binding = WiaSettingBindings.Bindings[DriverSettingKeys.LongDocument];

        var probe = KeyedSettingNegotiator.QueryCaps(access, Candidates, WiaSettingBindings.Gaps)
            .Single(x => x.Key == DriverSettingKeys.LongDocument);
        var result = Assert.Single(KeyedSettingNegotiator.Apply(access, "WIA", Candidates,
            WiaSettingBindings.Gaps, LongDocument));
        var write = Assert.Throws<InvalidOperationException>(() => access.Write(binding, 1));

        Assert.Equal(DriverProcessingCapabilityState.QueryFailed, probe.State);
        Assert.Contains("RPC server unavailable", probe.Message);
        Assert.Equal(DriverProcessingStatus.Failed, result.Status);
        Assert.Contains("RPC server unavailable", result.Message);
        Assert.Contains("RPC server unavailable", write.Message);
    }

    [Theory]
    [InlineData(PaperSource.Auto, true, true, PaperSource.Flatbed)]
    [InlineData(PaperSource.Auto, false, true, PaperSource.Feeder)]
    [InlineData(PaperSource.Auto, false, false, PaperSource.Flatbed)]
    [InlineData(PaperSource.Feeder, true, true, PaperSource.Feeder)]
    [InlineData(PaperSource.Duplex, true, true, PaperSource.Duplex)]
    public void TheDryRunResolvesAutoToTheSourceAcquisitionScans(PaperSource requested, bool flatbed, bool feeder,
        PaperSource expected)
    {
        Assert.Equal(expected, WiaScanDriver.ResolvePaperSource(requested, flatbed, feeder));
    }
}
#endif
