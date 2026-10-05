#if !MACOS
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NTwain;
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// Reads every capability a TWAIN data source reports, including vendor-defined ones, without acquiring.
/// </summary>
/// <remarks>
/// The inventory only observes. It issues MSG_QUERYSUPPORT, MSG_GET and MSG_GETLABEL for each id listed in
/// CAP_SUPPORTEDCAPS and never sets or resets a value, so reading it does not change the source configuration. Each
/// capability is isolated: a source that rejects or corrupts one query still reports the others.
/// </remarks>
internal static class TwainCapabilityInventory
{
    // TWAIN reserves ids from CAP_CUSTOMBASE upward for vendor-defined capabilities.
    private const ushort CustomBase = 0x8000;

    public static DriverCapabilityInventory Read(DataSource source, ILogger? logger = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        var identity = ReadIdentity(source);
        List<CapabilityId> ids;
        try
        {
            ids = source.Capabilities.CapSupportedCaps.GetValues().Distinct().OrderBy(x => (ushort) x).ToList();
        }
        catch (Exception e)
        {
            logger?.LogDebug(e, "Couldn't read CAP_SUPPORTEDCAPS for the capability inventory");
            return new DriverCapabilityInventory
            {
                Protocol = DriverCapabilityProtocol.Twain,
                Identity = identity,
                Capabilities = ImmutableList<DriverCapabilityEntry>.Empty,
                FailureReason = $"CAP_SUPPORTEDCAPS could not be read: {e.Message}"
            };
        }

        var entries = ids.Select(id => ReadEntry(source, id, logger)).ToImmutableList();
        return new DriverCapabilityInventory
        {
            Protocol = DriverCapabilityProtocol.Twain,
            Identity = identity,
            Capabilities = entries,
            FailureReason = ids.Count == 0 ? "The source reported no supported capabilities." : null
        };
    }

    internal static DriverDeviceIdentity ReadIdentity(DataSource source)
    {
        var version = source.Version;
        return new DriverDeviceIdentity
        {
            Manufacturer = source.Manufacturer,
            ProductFamily = source.ProductFamily,
            ProductName = source.Name,
            DriverVersion = string.Format(CultureInfo.InvariantCulture, "{0}.{1}", version.Major, version.Minor),
            DriverVersionInfo = string.IsNullOrWhiteSpace(version.Info) ? null : version.Info,
            ProtocolVersion = source.ProtocolVersion?.ToString(2)
        };
    }

    private static DriverCapabilityEntry ReadEntry(DataSource source, CapabilityId id, ILogger? logger)
    {
        QuerySupports? supports = null;
        CapabilityReader? read = null;
        string? message = null;

        try
        {
            supports = source.Capabilities.QuerySupport(id);
        }
        catch (Exception e)
        {
            logger?.LogDebug(e, "MSG_QUERYSUPPORT failed for TWAIN capability {Capability}", id);
        }

        if (supports != QuerySupports.None)
        {
            try
            {
                read = source.Capabilities.GetValuesRaw(id);
                if (!IsValid(read))
                {
                    read = null;
                    message = $"MSG_GET failed (condition code {source.GetStatus().ConditionCode}).";
                }
            }
            catch (Exception e)
            {
                logger?.LogDebug(e, "MSG_GET failed for TWAIN capability {Capability}", id);
                read = null;
                message = $"MSG_GET returned unreadable data: {e.Message}";
            }
        }

        return ToEntry(id, supports, read, message, ReadLabel(source, id));
    }

    private static string? ReadLabel(DataSource source, CapabilityId id)
    {
        try
        {
            using var cap = new TWCapability(id);
            // ReadValue throws for an empty container, which the catch below treats as no label.
            if (source.DGControl.Capability.GetLabel(cap) != ReturnCode.Success ||
                cap.ContainerType != ContainerType.OneValue)
            {
                return null;
            }
            var label = CapabilityReader.ReadValue(cap).OneValue as string;
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch (Exception)
        {
            // Labels are optional diagnostics; many sources do not implement MSG_GETLABEL.
            return null;
        }
    }

    /// <summary>
    /// Converts what a source reported for one capability into an inventory entry.
    /// </summary>
    /// <param name="id">The capability id.</param>
    /// <param name="supports">The MSG_QUERYSUPPORT answer, or null when the source did not answer.</param>
    /// <param name="read">The MSG_GET container, or null when it failed or was not attempted.</param>
    /// <param name="message">Why the read failed, when it did.</param>
    /// <param name="label">The source's label for the capability, when it provides one.</param>
    internal static DriverCapabilityEntry ToEntry(CapabilityId id, QuerySupports? supports, CapabilityReader? read,
        string? message, string? label)
    {
        var rawId = (ushort) id;
        var entry = new DriverCapabilityEntry
        {
            Id = rawId,
            Name = rawId < CustomBase && Enum.IsDefined(typeof(CapabilityId), id) ? id.ToString() : null,
            Label = label,
            IsCustom = rawId >= CustomBase,
            Scope = DriverCapabilityScope.Source,
            State = GetState(supports, read),
            CanGet = Has(supports, QuerySupports.Get),
            CanGetCurrent = Has(supports, QuerySupports.GetCurrent),
            CanGetDefault = Has(supports, QuerySupports.GetDefault),
            CanSet = Has(supports, QuerySupports.Set),
            CanReset = Has(supports, QuerySupports.Reset),
            Message = message
        };
        if (read == null || !IsValid(read))
        {
            return entry;
        }

        var itemType = read.ItemType;
        entry = entry with
        {
            ContainerType = read.ContainerType.ToString(),
            ItemType = itemType.ToString()
        };
        switch (read.ContainerType)
        {
            case ContainerType.OneValue:
                return entry with { Current = ToValue(read.OneValue, itemType) };
            case ContainerType.Range:
                return entry with
                {
                    Current = ToValue(read.RangeCurrentValue, itemType),
                    Default = ToValue(read.RangeDefaultValue, itemType),
                    Minimum = ToValue(read.RangeMinValue, itemType),
                    Maximum = ToValue(read.RangeMaxValue, itemType),
                    Step = ToValue(read.RangeStepSize, itemType)
                };
            case ContainerType.Enum:
            {
                var values = read.CollectionValues ?? [];
                return entry with
                {
                    Values = ToValues(values, itemType),
                    Current = ValueAt(values, read.EnumCurrentIndex, itemType),
                    Default = ValueAt(values, read.EnumDefaultIndex, itemType)
                };
            }
            case ContainerType.Array:
                return entry with { Values = ToValues(read.CollectionValues ?? [], itemType) };
            default:
                return entry;
        }
    }

    /// <summary>
    /// Converts a value read from a TWAIN container. BOOL is read as a 16-bit integer, so the item type decides the
    /// kind rather than the CLR type.
    /// </summary>
    internal static DriverSettingValue? ToValue(object? value, ItemType itemType)
    {
        if (value == null)
        {
            return null;
        }

        switch (itemType)
        {
            case ItemType.Bool:
                return DriverSettingValue.FromBoolean(Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0);
            case ItemType.Int8:
            case ItemType.UInt8:
            case ItemType.Int16:
            case ItemType.UInt16:
            case ItemType.Int32:
            case ItemType.UInt32:
                return DriverSettingValue.FromInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ItemType.Fix32:
                return value is TWFix32 fix
                    ? DriverSettingValue.FromReal(fix.Whole + fix.Fraction / 65536d)
                    : DriverSettingValue.FromReal(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case ItemType.Frame:
                return value is TWFrame frame
                    ? DriverSettingValue.FromText(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                        ToDouble(frame.Left), ToDouble(frame.Top), ToDouble(frame.Right), ToDouble(frame.Bottom)))
                    : DriverSettingValue.FromText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            case ItemType.Handle:
                // A handle is process memory, not a value. Record that one was present without exposing it.
                return DriverSettingValue.FromText("(handle)");
            default:
                return DriverSettingValue.FromText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        }
    }

    private static ImmutableList<DriverSettingValue> ToValues(IEnumerable<object> values, ItemType itemType) =>
        values.Select(x => ToValue(x, itemType)).OfType<DriverSettingValue>().ToImmutableList();

    private static DriverSettingValue? ValueAt(IList<object> values, int index, ItemType itemType) =>
        index >= 0 && index < values.Count ? ToValue(values[index], itemType) : null;

    private static double ToDouble(TWFix32 value) => value.Whole + value.Fraction / 65536d;

    private static bool IsValid(CapabilityReader read) =>
        read.ContainerType is ContainerType.OneValue or ContainerType.Enum or ContainerType.Range or
            ContainerType.Array;

    private static DriverProcessingCapabilityState GetState(QuerySupports? supports, CapabilityReader? read)
    {
        if (supports == QuerySupports.None)
        {
            return DriverProcessingCapabilityState.Unsupported;
        }
        if (read == null || !IsValid(read))
        {
            return DriverProcessingCapabilityState.QueryFailed;
        }
        if (supports == null)
        {
            // Readable, but the source did not say whether it can be set.
            return DriverProcessingCapabilityState.Unknown;
        }
        return supports.Value.HasFlag(QuerySupports.Set)
            ? DriverProcessingCapabilityState.Writable
            : DriverProcessingCapabilityState.ReadOnly;
    }

    private static bool? Has(QuerySupports? supports, QuerySupports flag) =>
        supports.HasValue ? supports.Value.HasFlag(flag) : null;
}
#endif
