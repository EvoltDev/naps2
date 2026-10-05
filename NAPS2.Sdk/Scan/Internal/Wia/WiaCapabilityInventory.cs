#if !MACOS
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NAPS2.Wia;

namespace NAPS2.Scan.Internal.Wia;

/// <summary>
/// Reads every property a WIA device and scan item report, including private ones, without acquiring.
/// </summary>
/// <remarks>
/// The inventory only reads values and property attributes; it never writes. Private (vendor-defined) properties are
/// listed by id with the name the driver gives them as a label only — a private property's meaning is never inferred
/// from its name.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WiaCapabilityInventory
{
    // WIA_DIP_DRIVER_VERSION; not defined by the NAPS2.Wia wrapper.
    private const int DriverVersionPropertyId = 15;

    // Ids from WIA_PRIVATE_DEVPROP and WIA_PRIVATE_ITEMPROP upward are reserved for vendor-defined properties.
    internal const int PrivateDevicePropertyBase = 38914;
    internal const int PrivateItemPropertyBase = 71682;

    public static DriverCapabilityInventory Read(WiaDevice device, WiaItemBase? item, ILogger? logger = null)
    {
        if (device == null) throw new ArgumentNullException(nameof(device));

        var entries = ImmutableList.CreateBuilder<DriverCapabilityEntry>();
        var failures = new List<string>();
        ReadProperties(device, DriverCapabilityScope.Device, entries, failures, logger);
        if (item != null)
        {
            ReadProperties(item, DriverCapabilityScope.Item, entries, failures, logger);
        }

        return new DriverCapabilityInventory
        {
            Protocol = DriverCapabilityProtocol.Wia,
            Identity = new DriverDeviceIdentity
            {
                Manufacturer = ReadString(device, WiaPropertyId.DIP_VEND_DESC),
                ProductName = ReadString(device, WiaPropertyId.DIP_DEV_DESC),
                DriverVersion = ReadString(device, DriverVersionPropertyId),
                ProtocolVersion = ReadString(device, WiaPropertyId.DIP_WIA_VERSION)
            },
            Capabilities = entries.ToImmutable(),
            FailureReason = failures.Count == 0 ? null : string.Join(" ", failures)
        };
    }

    private static void ReadProperties(WiaItemBase source, DriverCapabilityScope scope,
        ImmutableList<DriverCapabilityEntry>.Builder entries, List<string> failures, ILogger? logger)
    {
        try
        {
            foreach (var property in source.Properties)
            {
                entries.Add(ReadEntry(property, scope, logger));
            }
        }
        catch (Exception e)
        {
            logger?.LogDebug(e, "Could not enumerate WIA {Scope} properties for the capability inventory", scope);
            failures.Add($"The WIA {scope.ToString().ToLowerInvariant()} properties could not be enumerated: " +
                         e.Message);
        }
    }

    private static DriverCapabilityEntry ReadEntry(WiaProperty property, DriverCapabilityScope scope,
        ILogger? logger)
    {
        int id;
        string? label = null;
        try
        {
            id = property.Id;
            label = string.IsNullOrWhiteSpace(property.Name) ? null : property.Name;
            var attributes = property.Attributes;
            var flags = attributes.Flags;
            var canRead = flags.HasFlag(WiaPropertyFlags.Read);
            object? value = canRead ? property.Value : null;
            return ToEntry(id, label, scope, property.Type, flags, value, attributes.Min, attributes.Max,
                attributes.Step, attributes.Nom, attributes.Values);
        }
        catch (Exception e)
        {
            logger?.LogDebug(e, "Could not read a WIA property for the capability inventory");
            id = SafeId(property);
            return new DriverCapabilityEntry
            {
                Id = id,
                Label = label,
                IsCustom = IsPrivate(id, scope),
                Scope = scope,
                State = DriverProcessingCapabilityState.QueryFailed,
                Message = $"The property could not be read: {e.Message}"
            };
        }
    }

    /// <summary>
    /// Converts one WIA property observation into an inventory entry.
    /// </summary>
    internal static DriverCapabilityEntry ToEntry(int id, string? label, DriverCapabilityScope scope, ushort type,
        WiaPropertyFlags flags, object? value, int minimum, int maximum, int step, int nominal, object[]? values)
    {
        var canRead = flags.HasFlag(WiaPropertyFlags.Read);
        var canWrite = flags.HasFlag(WiaPropertyFlags.Write);
        var entry = new DriverCapabilityEntry
        {
            Id = id,
            Label = label,
            IsCustom = IsPrivate(id, scope),
            Scope = scope,
            State = canRead
                ? canWrite ? DriverProcessingCapabilityState.Writable : DriverProcessingCapabilityState.ReadOnly
                : canWrite
                    ? DriverProcessingCapabilityState.QueryFailed
                    : DriverProcessingCapabilityState.Unsupported,
            CanGet = canRead,
            CanGetCurrent = canRead,
            CanSet = canWrite,
            ContainerType = GetContainerType(flags),
            ItemType = GetTypeName(type),
            Current = ToValue(value)
        };

        // Range and list attributes describe integer properties. Other types carry no usable constraint values.
        if (flags.HasFlag(WiaPropertyFlags.Range))
        {
            return entry with
            {
                Minimum = DriverSettingValue.FromInteger(minimum),
                Maximum = DriverSettingValue.FromInteger(maximum),
                Step = DriverSettingValue.FromInteger(step),
                Default = DriverSettingValue.FromInteger(nominal)
            };
        }
        if (flags.HasFlag(WiaPropertyFlags.List) || flags.HasFlag(WiaPropertyFlags.Flag))
        {
            return entry with
            {
                Values = (values ?? []).Select(ToValue).OfType<DriverSettingValue>().ToImmutableList(),
                Default = ToValue(nominal)
            };
        }
        return entry;
    }

    internal static DriverSettingValue? ToValue(object? value) => value switch
    {
        null => null,
        bool b => DriverSettingValue.FromBoolean(b),
        sbyte or byte or short or ushort or int or uint or long =>
            DriverSettingValue.FromInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        // A UI8 above long.MaxValue cannot be an integer value; keep its exact digits as text.
        ulong u => u <= long.MaxValue
            ? DriverSettingValue.FromInteger((long) u)
            : DriverSettingValue.FromText(u.ToString(CultureInfo.InvariantCulture)),
        float or double or decimal => DriverSettingValue.FromReal(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        string s => DriverSettingValue.FromText(s),
        Guid g => DriverSettingValue.FromText(g.ToString("B")),
        _ => DriverSettingValue.FromText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
    };

    internal static bool IsPrivate(int id, DriverCapabilityScope scope) =>
        scope == DriverCapabilityScope.Item ? id >= PrivateItemPropertyBase : id >= PrivateDevicePropertyBase;

    private static string GetContainerType(WiaPropertyFlags flags)
    {
        if (flags.HasFlag(WiaPropertyFlags.Range)) return "Range";
        if (flags.HasFlag(WiaPropertyFlags.List)) return "List";
        if (flags.HasFlag(WiaPropertyFlags.Flag)) return "Flag";
        return "None";
    }

    // VARENUM names for the property types WIA uses.
    private static string GetTypeName(ushort type) => type switch
    {
        2 => "I2",
        3 => "I4",
        4 => "R4",
        5 => "R8",
        8 => "BSTR",
        11 => "BOOL",
        17 => "UI1",
        18 => "UI2",
        19 => "UI4",
        72 => "CLSID",
        _ => $"VT {type}"
    };

    private static string? ReadString(WiaItemBase source, int propertyId)
    {
        try
        {
            var value = source.Properties.GetOrNull(propertyId)?.Value;
            var text = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int SafeId(WiaProperty property)
    {
        try
        {
            return property.Id;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
#endif
