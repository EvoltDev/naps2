#if !MACOS
using NAPS2.Scan;
using NAPS2.Scan.Internal.Wia;
using NAPS2.Wia;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class WiaCapabilityInventoryTests
{
    private const ushort I4 = 3;
    private const ushort Bstr = 8;

    [Fact]
    public void RangePropertyReportsBoundsAndNominalAsDefault()
    {
        var entry = WiaCapabilityInventory.ToEntry(6146, "Brightness", DriverCapabilityScope.Item, I4,
            WiaPropertyFlags.ReadWrite | WiaPropertyFlags.Range, 10, -1000, 1000, 1, 0, null);

        Assert.Equal(DriverProcessingCapabilityState.Writable, entry.State);
        Assert.False(entry.IsCustom);
        Assert.Null(entry.Name);
        Assert.Equal("Brightness", entry.Label);
        Assert.Equal("Range", entry.ContainerType);
        Assert.Equal("I4", entry.ItemType);
        Assert.Equal(10L, entry.Current!.IntegerValue);
        Assert.Equal(-1000L, entry.Minimum!.IntegerValue);
        Assert.Equal(1000L, entry.Maximum!.IntegerValue);
        Assert.Equal(0L, entry.Default!.IntegerValue);
    }

    [Fact]
    public void ListPropertyReportsItsValues()
    {
        var entry = WiaCapabilityInventory.ToEntry(4103, "Data Type", DriverCapabilityScope.Item, I4,
            WiaPropertyFlags.ReadWrite | WiaPropertyFlags.List, 3, 0, 0, 0, 3, [0, 2, 3]);

        Assert.Equal("List", entry.ContainerType);
        Assert.Equal([0L, 2L, 3L], entry.Values!.Select(x => x.IntegerValue!.Value));
        Assert.Equal(3L, entry.Default!.IntegerValue);
    }

    [Fact]
    public void PrivatePropertiesAreCustomOnlyFromTheirScopeBase()
    {
        var privateItem = WiaCapabilityInventory.ToEntry(WiaCapabilityInventory.PrivateItemPropertyBase + 5,
            "Vendor Setting", DriverCapabilityScope.Item, I4, WiaPropertyFlags.ReadWrite | WiaPropertyFlags.None, 1,
            0, 0, 0, 0, null);
        var privateDevice = WiaCapabilityInventory.ToEntry(WiaCapabilityInventory.PrivateDevicePropertyBase,
            "Vendor Device Setting", DriverCapabilityScope.Device, Bstr, WiaPropertyFlags.Read, "x", 0, 0, 0, 0,
            null);
        var standardItemAboveDeviceBase = WiaCapabilityInventory.ToEntry(
            WiaCapabilityInventory.PrivateDevicePropertyBase + 1, null, DriverCapabilityScope.Item, I4,
            WiaPropertyFlags.Read, 0, 0, 0, 0, 0, null);

        Assert.True(privateItem.IsCustom);
        Assert.True(privateDevice.IsCustom);
        Assert.Equal(DriverProcessingCapabilityState.ReadOnly, privateDevice.State);
        Assert.Equal("x", privateDevice.Current!.TextValue);
        Assert.False(standardItemAboveDeviceBase.IsCustom);
    }

    [Fact]
    public void WriteOnlyPropertyIsNotReportedAsReadable()
    {
        var entry = WiaCapabilityInventory.ToEntry(4170, null, DriverCapabilityScope.Item, I4,
            WiaPropertyFlags.Write, null, 0, 0, 0, 0, null);

        Assert.Equal(DriverProcessingCapabilityState.QueryFailed, entry.State);
        Assert.Equal(false, entry.CanGet);
        Assert.Null(entry.Current);
    }
}
#endif
