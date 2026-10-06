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

    [Fact]
    public void AnAbsentPropertyIsUnsupported()
    {
        var access = new WiaSettingAccess(_ => null);

        var result = Assert.Single(KeyedSettingNegotiator.Apply(access, "WIA", WiaSettingBindings.Bindings,
            WiaSettingBindings.Gaps, LongDocument));

        Assert.Equal(DriverProcessingStatus.Unsupported, result.Status);
    }

    [Fact]
    public void AFailedPropertyLookupIsAFailureNotUnsupported()
    {
        var access = new WiaSettingAccess(_ => throw new InvalidOperationException("RPC server unavailable"));
        var binding = WiaSettingBindings.Bindings[DriverSettingKeys.LongDocument];

        var probe = KeyedSettingNegotiator.QueryCaps(access, WiaSettingBindings.Bindings, WiaSettingBindings.Gaps)
            .Single(x => x.Key == DriverSettingKeys.LongDocument);
        var result = Assert.Single(KeyedSettingNegotiator.Apply(access, "WIA", WiaSettingBindings.Bindings,
            WiaSettingBindings.Gaps, LongDocument));
        var write = Assert.Throws<InvalidOperationException>(() => access.Write(binding, 1));

        Assert.Equal(DriverProcessingCapabilityState.QueryFailed, probe.State);
        Assert.Contains("RPC server unavailable", probe.Message);
        Assert.Equal(DriverProcessingStatus.Failed, result.Status);
        Assert.Contains("RPC server unavailable", result.Message);
        Assert.Contains("RPC server unavailable", write.Message);
    }
}
#endif
