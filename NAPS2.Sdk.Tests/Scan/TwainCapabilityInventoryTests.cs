#if !MACOS
using NAPS2.Scan;
using NAPS2.Scan.Internal.Twain;
using NTwain;
using NTwain.Data;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class TwainCapabilityInventoryTests
{
    // Kodak ICAP_DOCUMENTTYPE from kdscust.h; any id at or above CAP_CUSTOMBASE is vendor-defined.
    private const CapabilityId KodakDocumentType = (CapabilityId) 0x80AC;

    [Fact]
    public void CustomEnumerationKeepsItsIdValuesAndIndices()
    {
        using var cap = new TWCapability(KodakDocumentType, new TWEnumeration
        {
            ItemType = ItemType.UInt16,
            ItemList = [(ushort) 0, (ushort) 1, (ushort) 2, (ushort) 3],
            CurrentIndex = 2,
            DefaultIndex = 1
        });
        var supports = QuerySupports.Get | QuerySupports.GetCurrent | QuerySupports.GetDefault | QuerySupports.Set;

        var entry = TwainCapabilityInventory.ToEntry(KodakDocumentType, supports, CapabilityReader.ReadValue(cap),
            null, "Document Type");

        Assert.Equal(0x80AC, entry.Id);
        Assert.True(entry.IsCustom);
        Assert.Null(entry.Name);
        Assert.Equal("Document Type", entry.Label);
        Assert.Equal(DriverProcessingCapabilityState.Writable, entry.State);
        Assert.Equal(true, entry.CanSet);
        Assert.Equal(false, entry.CanReset);
        Assert.Equal("Enum", entry.ContainerType);
        Assert.Equal("UInt16", entry.ItemType);
        Assert.Equal([0L, 1L, 2L, 3L], entry.Values!.Select(x => x.IntegerValue!.Value));
        Assert.Equal(2L, entry.Current!.IntegerValue);
        Assert.Equal(1L, entry.Default!.IntegerValue);
    }

    [Fact]
    public void BoolItemsBecomeBooleansAndStandardIdsKeepTheirName()
    {
        using var cap = new TWCapability(CapabilityId.ICapAutomaticDeskew,
            new TWOneValue { ItemType = ItemType.Bool, Item = 1 });

        var entry = TwainCapabilityInventory.ToEntry(CapabilityId.ICapAutomaticDeskew,
            QuerySupports.Get | QuerySupports.GetCurrent, CapabilityReader.ReadValue(cap), null, null);

        Assert.False(entry.IsCustom);
        Assert.Equal(nameof(CapabilityId.ICapAutomaticDeskew), entry.Name);
        Assert.Equal(DriverProcessingCapabilityState.ReadOnly, entry.State);
        Assert.Equal(DriverSettingValueKind.Boolean, entry.Current!.Kind);
        Assert.True(entry.Current.BooleanValue);
    }

    [Fact]
    public void RangeReportsBoundsStepAndDefault()
    {
        using var cap = new TWCapability(CapabilityId.ICapBrightness, new TWRange
        {
            ItemType = ItemType.Int32,
            MinValue = unchecked((uint) -1000),
            MaxValue = 1000,
            StepSize = 20,
            DefaultValue = 0,
            CurrentValue = 40
        });

        var entry = TwainCapabilityInventory.ToEntry(CapabilityId.ICapBrightness,
            QuerySupports.Get | QuerySupports.Set, CapabilityReader.ReadValue(cap), null, null);

        Assert.Equal("Range", entry.ContainerType);
        Assert.Equal(-1000L, entry.Minimum!.IntegerValue);
        Assert.Equal(1000L, entry.Maximum!.IntegerValue);
        Assert.Equal(20L, entry.Step!.IntegerValue);
        Assert.Equal(0L, entry.Default!.IntegerValue);
        Assert.Equal(40L, entry.Current!.IntegerValue);
    }

    [Fact]
    public void Fix32ValuesBecomeReals()
    {
        TWFix32 gamma = 2.25f;

        var value = TwainCapabilityInventory.ToValue(gamma, ItemType.Fix32);

        Assert.Equal(DriverSettingValueKind.Real, value!.Kind);
        Assert.Equal(2.25, value.RealValue!.Value, 3);
    }

    [Fact]
    public void ReadableCapabilityWithoutQuerySupportHasUnknownAccess()
    {
        using var cap = new TWCapability(CapabilityId.ICapGamma, new TWOneValue { ItemType = ItemType.UInt16, Item = 1 });

        var entry = TwainCapabilityInventory.ToEntry(CapabilityId.ICapGamma, null, CapabilityReader.ReadValue(cap),
            null, null);

        Assert.Equal(DriverProcessingCapabilityState.Unknown, entry.State);
        Assert.Null(entry.CanGet);
        Assert.Null(entry.CanSet);
        Assert.NotNull(entry.Current);
    }

    [Fact]
    public void UnsupportedAndFailedReadsAreKeptApart()
    {
        var unsupported = TwainCapabilityInventory.ToEntry(CapabilityId.ICapGamma, QuerySupports.None, null, null,
            null);
        var failed = TwainCapabilityInventory.ToEntry(KodakDocumentType, QuerySupports.Get, null,
            "MSG_GET failed (condition code BadCap).", null);

        Assert.Equal(DriverProcessingCapabilityState.Unsupported, unsupported.State);
        Assert.Equal(false, unsupported.CanGet);
        Assert.Equal(DriverProcessingCapabilityState.QueryFailed, failed.State);
        Assert.Equal("MSG_GET failed (condition code BadCap).", failed.Message);
        Assert.Null(failed.ContainerType);
        Assert.Null(failed.Current);
    }

    [Fact]
    public void NativeUiNeutralizesWellFormedKeyedRequestsOnly()
    {
        var options = new DriverProcessingOptions
        {
            Deskew = true,
            Settings =
            [
                new DriverSettingRequest { Key = "gamma", Value = DriverSettingValue.FromReal(2.2) },
                new DriverSettingRequest { Key = "deskew", Value = DriverSettingValue.FromBoolean(true) }
            ]
        };

        var result = TwainDriverProcessing.NativeUiResult(options);

        Assert.Equal(["Deskew", "gamma"], result.NeutralizedSettings.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2.2, result.RequestedSettings["gamma"]);
    }
}
#endif
