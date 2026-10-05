using System.Collections.Immutable;

namespace NAPS2.Scan;

/// <summary>
/// The protocol a capability inventory was read through.
/// </summary>
public enum DriverCapabilityProtocol
{
    Unknown,
    Twain,
    Wia
}

/// <summary>
/// The object a capability was read from. TWAIN capabilities belong to the data source; WIA properties belong to the
/// device root item or to a scan item.
/// </summary>
public enum DriverCapabilityScope
{
    Source,
    Device,
    Item
}

/// <summary>
/// The identity a driver reports for itself. This is what vendor bindings are matched against.
/// </summary>
public sealed record DriverDeviceIdentity
{
    public string? Manufacturer { get; init; }

    public string? ProductFamily { get; init; }

    public string? ProductName { get; init; }

    /// <summary>
    /// The driver version in the form the protocol reports it, for example "4.10" for TWAIN major and minor numbers.
    /// </summary>
    public string? DriverVersion { get; init; }

    /// <summary>
    /// Free-form version information reported by the driver, such as the TWAIN identity's version info string.
    /// </summary>
    public string? DriverVersionInfo { get; init; }

    /// <summary>
    /// The protocol version the driver supports, for example the TWAIN protocol major and minor numbers.
    /// </summary>
    public string? ProtocolVersion { get; init; }
}

/// <summary>
/// One capability or property as observed on a device, without interpretation.
/// </summary>
/// <remarks>
/// An entry describes what the driver reports, not what EVOSCAN can do with it. Custom entries are listed with their
/// numeric id only; a name or meaning is assigned by a verified binding, never inferred from a label.
/// </remarks>
public sealed record DriverCapabilityEntry
{
    /// <summary>
    /// The TWAIN capability id or WIA property id.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// The standard name of the capability or property, when the protocol defines one.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The label the driver reports, when it provides one. Labels are for diagnostics only and may be localized.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Gets whether the id is in the protocol's vendor-defined range.
    /// </summary>
    public bool IsCustom { get; init; }

    public DriverCapabilityScope Scope { get; init; }

    public DriverProcessingCapabilityState State { get; init; }

    // The access flags are null when the driver does not report its supported operations (TWAIN MSG_QUERYSUPPORT
    // is optional). A readable capability with unknown access must not be treated as read-only or writable.

    public bool? CanGet { get; init; }

    public bool? CanGetCurrent { get; init; }

    public bool? CanGetDefault { get; init; }

    public bool? CanSet { get; init; }

    public bool? CanReset { get; init; }

    /// <summary>
    /// How the driver returned its values, for example a TWAIN container type ("OneValue", "Enum", "Range", "Array")
    /// or the WIA attribute kind ("Range", "List", "Flag", "None").
    /// </summary>
    public string? ContainerType { get; init; }

    /// <summary>
    /// The native item type, for example a TWAIN item type ("UInt16", "Fix32") or a WIA property type ("I4").
    /// </summary>
    public string? ItemType { get; init; }

    public DriverSettingValue? Current { get; init; }

    public DriverSettingValue? Default { get; init; }

    public DriverSettingValue? Minimum { get; init; }

    public DriverSettingValue? Maximum { get; init; }

    public DriverSettingValue? Step { get; init; }

    /// <summary>
    /// The values the driver offers, for enumeration, list and array containers.
    /// </summary>
    public ImmutableList<DriverSettingValue>? Values { get; init; }

    /// <summary>
    /// Why the entry could not be read completely, such as a TWAIN condition code.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Every capability a driver reports, read without acquiring.
/// </summary>
/// <remarks>
/// The inventory is requested with <see cref="ScanOptions.IncludeDriverCapabilityInventory"/> and returned in
/// <see cref="ScanCaps.DriverCapabilityInventory"/>. It is the evidence used to decide whether a binding applies to a
/// device. A failure to read one capability is recorded on that entry; a failure to enumerate at all is recorded in
/// <see cref="FailureReason"/>.
/// </remarks>
public sealed record DriverCapabilityInventory
{
    public DriverCapabilityProtocol Protocol { get; init; }

    public DriverDeviceIdentity? Identity { get; init; }

    public ImmutableList<DriverCapabilityEntry>? Capabilities { get; init; }

    public string? FailureReason { get; init; }
}
