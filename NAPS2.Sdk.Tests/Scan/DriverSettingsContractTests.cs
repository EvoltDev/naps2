using System.Collections.Immutable;
using NAPS2.Remoting.Worker;
using NAPS2.Scan;
using NAPS2.Scan.Internal;
using NAPS2.Serialization;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class DriverSettingsContractTests
{
    [Fact]
    public void SettingValuesExposeOnlyTheirKind()
    {
        Assert.Equal(true, DriverSettingValue.FromBoolean(true).ToObject());
        Assert.Equal(42L, DriverSettingValue.FromInteger(42).ToObject());
        Assert.Equal(1.5, DriverSettingValue.FromReal(1.5).ToObject());
        Assert.Equal("auto", DriverSettingValue.FromText("auto").ToObject());

        var mismatched = new DriverSettingValue { Kind = DriverSettingValueKind.Integer, RealValue = 2.0 };
        Assert.False(mismatched.HasValue);
        Assert.Null(mismatched.ToObject());
    }

    [Fact]
    public void KeyedRequestsRoundTripAndCountAsRequests()
    {
        var original = new ScanOptions
        {
            IncludeDriverCapabilityInventory = true,
            TwainOptions = new TwainOptions
            {
                ProcessingOptions = new DriverProcessingOptions
                {
                    Settings =
                    [
                        new DriverSettingRequest { Key = "gamma", Value = DriverSettingValue.FromReal(2.2) },
                        new DriverSettingRequest { Key = "edgeFill", Value = DriverSettingValue.FromText("white") }
                    ]
                }
            }
        };

        var serializer = new XmlSerializer<ScanOptions>();
        var copy = serializer.DeserializeFromXDocument(serializer.SerializeToXDocument(original));

        Assert.NotNull(copy);
        Assert.True(copy.IncludeDriverCapabilityInventory);
        var processing = copy.TwainOptions.ProcessingOptions;
        Assert.True(processing.HasRequests);
        Assert.Equal(2, processing.Settings.Count);
        Assert.Equal("gamma", processing.Settings[0].Key);
        Assert.Equal(DriverSettingValueKind.Real, processing.Settings[0].Value!.Kind);
        Assert.Equal(2.2, processing.Settings[0].Value!.RealValue);
        Assert.Equal("white", processing.Settings[1].Value!.TextValue);
        Assert.False(copy.WiaOptions.ProcessingOptions.HasRequests);
    }

    [Fact]
    public void KeyedRequestValidationRejectsMalformedRequestsAndKeepsTheFirstDuplicate()
    {
        var options = new DriverProcessingOptions
        {
            Settings =
            [
                new DriverSettingRequest { Key = "gamma", Value = DriverSettingValue.FromReal(2.2) },
                new DriverSettingRequest { Key = " ", Value = DriverSettingValue.FromBoolean(true) },
                new DriverSettingRequest { Key = "brightness", Value = DriverSettingValue.FromInteger(10) },
                new DriverSettingRequest { Key = "gamma", Value = DriverSettingValue.FromReal(1.0) },
                new DriverSettingRequest { Key = "threshold" }
            ]
        };

        var read = KeyedDriverSettings.Read(options).ToList();

        Assert.Equal(5, read.Count);
        Assert.Null(read[0].Rejection);
        Assert.Equal(2.2, read[0].RequestedValue);
        Assert.NotNull(read[1].Rejection);
        Assert.Contains("typed", read[2].Rejection);
        Assert.Contains("more than once", read[3].Rejection);
        Assert.Contains("no value", read[4].Rejection);
    }

    [Fact]
    public void ProcessingSettingEvidenceRoundTripsThroughXmlAndTheWorkerWire()
    {
        var original = new DriverProcessingResult
        {
            RequestedSettings = new Dictionary<string, object?> { ["gamma"] = 2.2 },
            EffectiveSettings = new Dictionary<string, object?> { ["gamma"] = 2.0 },
            Settings =
            [
                new DriverProcessingSetting
                {
                    Name = "gamma",
                    Status = DriverProcessingStatus.Adjusted,
                    RequestedValue = 2.2,
                    EffectiveValue = 2.0,
                    Binding = "TWAIN ICAP_GAMMA",
                    ExecutionLocation = DriverExecutionLocation.VendorSoftware,
                    Evidence = "Host processing is enabled."
                }
            ]
        };

        var serializer = new XmlSerializer<DriverProcessingResult>();
        var xmlCopy = serializer.DeserializeFromXDocument(serializer.SerializeToXDocument(original))!;
        var wireCopy = RawWorkerWireMapper.FromConfiguration(RawWorkerWireMapper.ToConfiguration(original));

        foreach (var copy in new[] { xmlCopy, wireCopy })
        {
            var setting = Assert.Single(copy.Settings);
            Assert.Equal(DriverProcessingStatus.Adjusted, setting.Status);
            Assert.Equal("TWAIN ICAP_GAMMA", setting.Binding);
            Assert.Equal(DriverExecutionLocation.VendorSoftware, setting.ExecutionLocation);
            Assert.Equal("Host processing is enabled.", setting.Evidence);
        }
    }

    [Fact]
    public void WireDefaultsLeaveEvidenceEmptyAndLocationUnknown()
    {
        var original = new DriverProcessingResult
        {
            Settings = [new DriverProcessingSetting { Name = "deskew", Status = DriverProcessingStatus.Applied }]
        };

        var configuration = RawWorkerWireMapper.ToConfiguration(original);
        configuration.Settings[0].Status = 99;
        configuration.Settings[0].ExecutionLocation = 99;
        var copy = Assert.Single(RawWorkerWireMapper.FromConfiguration(configuration).Settings);

        Assert.Equal(DriverProcessingStatus.Unknown, copy.Status);
        Assert.Null(copy.Binding);
        Assert.Null(copy.Evidence);
        Assert.Equal(DriverExecutionLocation.Unknown, copy.ExecutionLocation);
    }

    [Fact]
    public void CapabilityInventoryRoundTripsWithUnknownAccessKeptDistinctFromFalse()
    {
        var original = new ScanCaps
        {
            DriverCapabilityInventory = new DriverCapabilityInventory
            {
                Protocol = DriverCapabilityProtocol.Twain,
                Identity = new DriverDeviceIdentity
                {
                    Manufacturer = "Kodak",
                    ProductFamily = "i4000",
                    ProductName = "KODAK Scanner: i4250",
                    DriverVersion = "4.10",
                    DriverVersionInfo = "v4.10.0",
                    ProtocolVersion = "2.1"
                },
                Capabilities =
                [
                    new DriverCapabilityEntry
                    {
                        Id = 0x80AC,
                        IsCustom = true,
                        Label = "Document Type",
                        State = DriverProcessingCapabilityState.Writable,
                        CanGet = true,
                        CanSet = true,
                        ContainerType = "Enum",
                        ItemType = "UInt16",
                        Current = DriverSettingValue.FromInteger(1),
                        Default = DriverSettingValue.FromInteger(1),
                        Values = ImmutableList.Create(
                            DriverSettingValue.FromInteger(0),
                            DriverSettingValue.FromInteger(1))
                    },
                    new DriverCapabilityEntry
                    {
                        Id = 0x1118,
                        Name = "IGamma",
                        State = DriverProcessingCapabilityState.Unknown,
                        CanGet = null,
                        CanSet = null,
                        ContainerType = "Range",
                        ItemType = "Fix32",
                        Minimum = DriverSettingValue.FromReal(0.5),
                        Maximum = DriverSettingValue.FromReal(4.0)
                    }
                ]
            }
        };

        var serializer = new XmlSerializer<ScanCaps>();
        var copy = serializer.DeserializeFromXDocument(serializer.SerializeToXDocument(original));

        var inventory = copy!.DriverCapabilityInventory!;
        Assert.Equal(DriverCapabilityProtocol.Twain, inventory.Protocol);
        Assert.Equal("4.10", inventory.Identity!.DriverVersion);
        Assert.Equal("KODAK Scanner: i4250", inventory.Identity.ProductName);
        Assert.Equal(2, inventory.Capabilities!.Count);

        var custom = inventory.Capabilities[0];
        Assert.True(custom.IsCustom);
        Assert.Equal(0x80AC, custom.Id);
        Assert.Equal(DriverProcessingCapabilityState.Writable, custom.State);
        Assert.Equal(true, custom.CanSet);
        Assert.Equal([0L, 1L], custom.Values!.Select(x => x.IntegerValue!.Value));
        Assert.Equal(1L, custom.Current!.IntegerValue);

        var unknownAccess = inventory.Capabilities[1];
        Assert.Null(unknownAccess.CanGet);
        Assert.Null(unknownAccess.CanSet);
        Assert.Equal(4.0, unknownAccess.Maximum!.RealValue);
    }

    [Fact]
    public void DryRunRequestAndResultRoundTrip()
    {
        var options = new ScanOptions { ProbeDriverProcessing = true };
        var caps = new ScanCaps
        {
            DriverProcessingProbe = new DriverProcessingResult
            {
                Settings =
                [
                    new DriverProcessingSetting
                    {
                        Name = "edgeFill",
                        Status = DriverProcessingStatus.Applied,
                        RequestedValue = "white",
                        EffectiveValue = "white",
                        Binding = "TWAIN ICAP_IMAGEEDGEFILL (0x8095)",
                        Evidence = "Kodak Alaris kdscust.h"
                    }
                ]
            }
        };

        var optionsCopy = new XmlSerializer<ScanOptions>().DeserializeFromXDocument(
            new XmlSerializer<ScanOptions>().SerializeToXDocument(options))!;
        var capsCopy = new XmlSerializer<ScanCaps>().DeserializeFromXDocument(
            new XmlSerializer<ScanCaps>().SerializeToXDocument(caps))!;

        Assert.True(optionsCopy.ProbeDriverProcessing);
        var setting = Assert.Single(capsCopy.DriverProcessingProbe!.Settings);
        Assert.Equal("edgeFill", setting.Name);
        Assert.Equal("Kodak Alaris kdscust.h", setting.Evidence);
    }

    [Fact]
    public void BooleanRequestsRoundTripWithFalseKeptDistinctFromAbsent()
    {
        var original = new DriverProcessingOptions
        {
            Settings =
            [
                new DriverSettingRequest { Key = "longDocument", Value = DriverSettingValue.FromBoolean(true) },
                new DriverSettingRequest { Key = "barcodeDetection", Value = DriverSettingValue.FromBoolean(false) },
                new DriverSettingRequest { Key = "threshold" }
            ]
        };

        var serializer = new XmlSerializer<DriverProcessingOptions>();
        var copy = serializer.DeserializeFromXDocument(serializer.SerializeToXDocument(original))!;

        Assert.Equal(3, copy.Settings.Count);
        Assert.Equal(DriverSettingValueKind.Boolean, copy.Settings[0].Value!.Kind);
        Assert.Equal(true, copy.Settings[0].Value!.ToObject());

        var falseValue = copy.Settings[1].Value!;
        Assert.Equal(DriverSettingValueKind.Boolean, falseValue.Kind);
        Assert.True(falseValue.HasValue);
        Assert.Equal(false, falseValue.ToObject());

        Assert.Null(copy.Settings[2].Value);
    }

    [Fact]
    public void CapabilityInventoryQueryFailuresRoundTrip()
    {
        var original = new ScanCaps
        {
            DriverCapabilityInventory = new DriverCapabilityInventory
            {
                Protocol = DriverCapabilityProtocol.Wia,
                FailureReason = "The item properties could not be enumerated.",
                Capabilities =
                [
                    new DriverCapabilityEntry
                    {
                        Id = 0x80AC,
                        IsCustom = true,
                        State = DriverProcessingCapabilityState.QueryFailed,
                        Message = "TWAIN condition code: BadCap"
                    },
                    new DriverCapabilityEntry
                    {
                        Id = 3088,
                        Name = "WIA_DPS_DOCUMENT_HANDLING_SELECT",
                        Scope = DriverCapabilityScope.Device,
                        State = DriverProcessingCapabilityState.ReadOnly,
                        Current = DriverSettingValue.FromBoolean(false),
                        Values = ImmutableList.Create(
                            DriverSettingValue.FromBoolean(false),
                            DriverSettingValue.FromBoolean(true))
                    }
                ]
            }
        };

        var serializer = new XmlSerializer<ScanCaps>();
        var inventory = serializer.DeserializeFromXDocument(serializer.SerializeToXDocument(original))!
            .DriverCapabilityInventory!;

        Assert.Equal("The item properties could not be enumerated.", inventory.FailureReason);
        Assert.Equal(2, inventory.Capabilities!.Count);

        var failed = inventory.Capabilities[0];
        Assert.Equal(DriverProcessingCapabilityState.QueryFailed, failed.State);
        Assert.Equal("TWAIN condition code: BadCap", failed.Message);
        Assert.Null(failed.Current);

        var boolean = inventory.Capabilities[1];
        Assert.Equal(DriverCapabilityScope.Device, boolean.Scope);
        Assert.Equal(false, boolean.Current!.ToObject());
        Assert.Equal([false, true], boolean.Values!.Select(x => (bool) x.ToObject()!));
    }
}
