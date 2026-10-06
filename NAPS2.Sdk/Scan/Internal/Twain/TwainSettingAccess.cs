#if !MACOS
using System.Globalization;
using Microsoft.Extensions.Logging;
using NTwain;
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// Reads and writes TWAIN capabilities by id for <see cref="KeyedSettingNegotiator"/>. Values are normalized at this
/// boundary: BOOL to <see cref="bool"/>, FIX32 to <see cref="double"/>, other integers to <see cref="int"/>.
/// </summary>
internal sealed class TwainSettingAccess : IDriverSettingAccess
{
    private readonly DataSource _source;
    private readonly ILogger? _logger;

    // The item type each probed capability reported, so a binding with alternative types writes in the device's type.
    private readonly Dictionary<int, ItemType> _observedTypes = new();

    public TwainSettingAccess(DataSource source, ILogger? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger;
    }

    public NativeProbe Probe(NativeSettingBinding binding)
    {
        var id = (CapabilityId) binding.NativeId;
        var supports = _source.Capabilities.QuerySupport(id);
        if (supports == QuerySupports.None)
        {
            return new NativeProbe { State = DriverProcessingCapabilityState.Unsupported };
        }

        CapabilityReader read;
        try
        {
            read = _source.Capabilities.GetValuesRaw(id);
        }
        catch (Exception e)
        {
            _logger?.LogDebug(e, "MSG_GET failed for TWAIN capability {Capability}", id);
            return new NativeProbe
            {
                State = DriverProcessingCapabilityState.QueryFailed,
                Message = $"{binding.NativeName} returned unreadable data."
            };
        }
        if (!IsValid(read))
        {
            var condition = _source.GetStatus().ConditionCode;
            return new NativeProbe
            {
                State = condition is ConditionCode.CapUnsupported or ConditionCode.BadCap
                    ? DriverProcessingCapabilityState.Unsupported
                    : DriverProcessingCapabilityState.QueryFailed,
                Message = $"MSG_GET for {binding.NativeName} failed (condition code {condition})."
            };
        }

        // A vendor-defined id is only trusted when the source reports the documented item type; another vendor, or
        // another driver version, may use the same id for something else.
        if (binding.RequireExactType && read.ItemType != ToItemType(binding.ValueType) &&
            !binding.AlternativeTypes.Any(x => read.ItemType == ToItemType(x)))
        {
            return new NativeProbe
            {
                State = DriverProcessingCapabilityState.Unsupported,
                Message = $"{binding.NativeName} reports item type {read.ItemType}, but the binding is documented " +
                          $"as {ToItemType(binding.ValueType)}."
            };
        }

        _observedTypes[binding.NativeId] = read.ItemType;
        var probe = new NativeProbe
        {
            State = supports == null
                ? DriverProcessingCapabilityState.Unknown
                : supports.Value.HasFlag(QuerySupports.Set)
                    ? DriverProcessingCapabilityState.Writable
                    : DriverProcessingCapabilityState.ReadOnly,
            Current = Current(read, binding.ValueType)
        };
        switch (read.ContainerType)
        {
            case ContainerType.Enum:
                probe = probe with { Values = Normalize(read.CollectionValues, binding.ValueType) };
                break;
            case ContainerType.Range:
                probe = probe with
                {
                    Minimum = Normalize(read.RangeMinValue, binding.ValueType),
                    Maximum = Normalize(read.RangeMaxValue, binding.ValueType)
                };
                break;
        }

        // The allowed values of a list setting can live in a separate capability, for example the supported
        // barcode types for the search priorities.
        if (binding.ValuesNativeId is { } valuesId)
        {
            try
            {
                var values = _source.Capabilities.GetValuesRaw((CapabilityId) valuesId);
                probe = probe with
                {
                    Values = IsValid(values) ? Normalize(Items(values), NativeValueType.UInt16) : null
                };
            }
            catch (Exception e)
            {
                _logger?.LogDebug(e, "MSG_GET failed for TWAIN capability {Capability}", valuesId);
                probe = probe with { Values = null };
            }
        }
        else if (binding.ValueType == NativeValueType.UInt16Array &&
                 read.ContainerType is ContainerType.Enum or ContainerType.Array)
        {
            probe = probe with { Values = Normalize(read.CollectionValues, NativeValueType.UInt16) };
        }
        return probe;
    }

    public NativeWriteResult Write(NativeSettingBinding binding, object nativeValue)
    {
        var id = (CapabilityId) binding.NativeId;
        var type = binding.ValueType;
        if (binding.AlternativeTypes.Count > 0 && _observedTypes.TryGetValue(binding.NativeId, out var observed) &&
            ToItemType(binding.ValueType) != observed)
        {
            foreach (var alternative in binding.AlternativeTypes)
            {
                if (ToItemType(alternative) == observed)
                {
                    type = alternative;
                    break;
                }
            }
        }
        using var cap = CreateCapability(id, type, nativeValue);
        var rc = _source.DGControl.Capability.Set(cap);
        if (rc == ReturnCode.Success)
        {
            return new NativeWriteResult(NativeWriteStatus.Accepted, null);
        }
        if (rc == ReturnCode.CheckStatus)
        {
            return new NativeWriteResult(NativeWriteStatus.AcceptedWithChange, null);
        }

        var condition = _source.GetStatus().ConditionCode;
        var message = $"MSG_SET for {binding.NativeName} failed (condition code {condition}).";
        return condition switch
        {
            ConditionCode.BadValue => new NativeWriteResult(NativeWriteStatus.Rejected, message),
            ConditionCode.CapUnsupported or ConditionCode.BadCap =>
                new NativeWriteResult(NativeWriteStatus.Unsupported, message),
            _ => new NativeWriteResult(NativeWriteStatus.Failed, message)
        };
    }

    public object? Read(NativeSettingBinding binding)
    {
        var id = (CapabilityId) binding.NativeId;
        if (binding.ValueType == NativeValueType.UInt16Array)
        {
            // MSG_GET on an array capability returns the current array.
            var read = _source.Capabilities.GetValuesRaw(id);
            return IsValid(read) ? Normalize(Items(read), NativeValueType.UInt16).ToArray() : null;
        }
        var current = _source.Capabilities.GetCurrent(id);
        return current == null ? null : Normalize(current, binding.ValueType);
    }

    internal static TWCapability CreateCapability(CapabilityId id, NativeValueType type, object value)
    {
        switch (type)
        {
            case NativeValueType.Fix32:
                TWFix32 fix = (float) KeyedSettingNegotiator.ToDouble(value);
                return new TWCapability(id, fix.ToOneValue());
            case NativeValueType.UInt16Array:
                return new TWCapability(id, new TWArray
                {
                    ItemType = ItemType.UInt16,
                    ItemList = ((object[]) value)
                        .Select(x => (object) Convert.ToUInt16(x, CultureInfo.InvariantCulture))
                        .ToArray()
                });
            default:
                var raw = KeyedSettingNegotiator.ToLong(value);
                return new TWCapability(id, new TWOneValue
                {
                    ItemType = ToItemType(type),
                    // TW_ONEVALUE stores every integer item in a 32-bit field; negative values keep their two's
                    // complement bits for the item width.
                    Item = type switch
                    {
                        NativeValueType.Int16 => (ushort) (short) raw,
                        NativeValueType.UInt16 or NativeValueType.Boolean => (ushort) raw,
                        _ => unchecked((uint) raw)
                    }
                });
        }
    }

    private static ItemType ToItemType(NativeValueType type) => type switch
    {
        NativeValueType.Boolean => ItemType.Bool,
        NativeValueType.Int16 => ItemType.Int16,
        NativeValueType.UInt16 => ItemType.UInt16,
        NativeValueType.Int32 => ItemType.Int32,
        NativeValueType.UInt32 => ItemType.UInt32,
        NativeValueType.Fix32 => ItemType.Fix32,
        _ => ItemType.UInt16
    };

    internal static object Normalize(object value, NativeValueType type) => type switch
    {
        NativeValueType.Boolean => KeyedSettingNegotiator.ToLong(value) != 0,
        NativeValueType.Fix32 => value is TWFix32 fix
            ? fix.Whole + fix.Fraction / 65536d
            : KeyedSettingNegotiator.ToDouble(value),
        _ => (int) KeyedSettingNegotiator.ToLong(value)
    };

    private static IReadOnlyList<object> Normalize(IEnumerable<object>? values, NativeValueType type) =>
        (values ?? []).Select(x => Normalize(x, type)).ToList();

    private static object? Current(CapabilityReader read, NativeValueType type)
    {
        if (type == NativeValueType.UInt16Array)
        {
            return read.ContainerType == ContainerType.Array
                ? Normalize(read.CollectionValues, NativeValueType.UInt16).ToArray()
                : null;
        }
        object? value = read.ContainerType switch
        {
            ContainerType.OneValue => read.OneValue,
            ContainerType.Range => read.RangeCurrentValue,
            ContainerType.Enum => read.CollectionValues != null && read.EnumCurrentIndex >= 0 &&
                                  read.EnumCurrentIndex < read.CollectionValues.Count
                ? read.CollectionValues[read.EnumCurrentIndex]
                : null,
            _ => null
        };
        return value == null ? null : Normalize(value, type);
    }

    private static IEnumerable<object> Items(CapabilityReader read) => read.ContainerType == ContainerType.OneValue
        ? [read.OneValue]
        : read.CollectionValues ?? [];

    private static bool IsValid(CapabilityReader read) =>
        read.ContainerType is ContainerType.OneValue or ContainerType.Enum or ContainerType.Range or
            ContainerType.Array;
}
#endif
