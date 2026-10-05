#if !MACOS
using NAPS2.Scan.Internal;
using NAPS2.Scan.Internal.Twain;
using NTwain;
using NTwain.Data;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class TwainSettingAccessTests
{
    [Fact]
    public void Fix32RequestsAreEncodedAsTwainFix32()
    {
        using var cap = TwainSettingAccess.CreateCapability(CapabilityId.ICapGamma, NativeValueType.Fix32, 2.2);

        var read = CapabilityReader.ReadValue(cap);

        Assert.Equal(ContainerType.OneValue, read.ContainerType);
        Assert.Equal(ItemType.Fix32, read.ItemType);
        Assert.Equal(2.2, (double) TwainSettingAccess.Normalize(read.OneValue, NativeValueType.Fix32), 3);
    }

    [Fact]
    public void ListRequestsAreEncodedAsUInt16Arrays()
    {
        using var cap = TwainSettingAccess.CreateCapability(CapabilityId.ICapBarcodeSearchPriorities,
            NativeValueType.UInt16Array, new object[] { (ushort) 20, (ushort) 4 });

        var read = CapabilityReader.ReadValue(cap);

        Assert.Equal(ContainerType.Array, read.ContainerType);
        Assert.Equal(ItemType.UInt16, read.ItemType);
        Assert.Equal(new object[] { (ushort) 20, (ushort) 4 }, read.CollectionValues);
    }

    [Theory]
    [InlineData((int) NativeValueType.Boolean, true, 1L)]
    [InlineData((int) NativeValueType.UInt16, 3, 3L)]
    [InlineData((int) NativeValueType.Int16, -10, -10L)]
    [InlineData((int) NativeValueType.Int32, -1000, -1000L)]
    public void ScalarRequestsKeepTheirItemTypeAndSign(int type, object value, long expected)
    {
        using var cap = TwainSettingAccess.CreateCapability((CapabilityId) 0x80AE, (NativeValueType) type, value);

        var read = CapabilityReader.ReadValue(cap);

        Assert.Equal(ContainerType.OneValue, read.ContainerType);
        Assert.Equal(expected, Convert.ToInt64(read.OneValue));
    }
}
#endif
