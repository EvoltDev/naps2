#if !MACOS
using Microsoft.Extensions.Logging;
using NAPS2.Wia;

namespace NAPS2.Scan.Internal.Wia;

/// <summary>
/// Reads and writes WIA properties by id for <see cref="KeyedSettingNegotiator"/>. A property is looked up on the
/// scan item first and then on the device; it is never selected by name.
/// </summary>
/// <remarks>
/// A property that is absent is unsupported, but a failure to enumerate the properties is not evidence of absence: the
/// lookup throws, and <see cref="KeyedSettingNegotiator"/> reports the probe as query-failed and the write as failed.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WiaSettingAccess : IDriverSettingAccess
{
    private const ushort I4 = 3; // VT_I4

    private readonly Func<int, WiaProperty?> _lookup;
    private readonly ILogger? _logger;

    public WiaSettingAccess(WiaDevice device, WiaItemBase item, ILogger? logger = null)
    {
        if (device == null) throw new ArgumentNullException(nameof(device));
        if (item == null) throw new ArgumentNullException(nameof(item));
        _lookup = id => item.Properties.GetOrNull(id) ?? device.Properties.GetOrNull(id);
        _logger = logger;
    }

    internal WiaSettingAccess(Func<int, WiaProperty?> lookup, ILogger? logger = null)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _logger = logger;
    }

    public NativeProbe Probe(NativeSettingBinding binding)
    {
        var property = Find(binding.NativeId);
        if (property == null)
        {
            return new NativeProbe
            {
                State = DriverProcessingCapabilityState.Unsupported,
                Message = $"The driver does not expose {binding.NativeName}."
            };
        }
        if (property.Type != I4)
        {
            return new NativeProbe
            {
                State = DriverProcessingCapabilityState.Unsupported,
                Message = $"{binding.NativeName} has an unexpected property type {property.Type}."
            };
        }

        var attributes = property.Attributes;
        var canRead = attributes.Flags.HasFlag(WiaPropertyFlags.Read);
        var canWrite = attributes.Flags.HasFlag(WiaPropertyFlags.Write);
        var probe = new NativeProbe
        {
            State = canRead
                ? canWrite ? DriverProcessingCapabilityState.Writable : DriverProcessingCapabilityState.ReadOnly
                : canWrite
                    ? DriverProcessingCapabilityState.Unknown
                    : DriverProcessingCapabilityState.Unsupported,
            Current = canRead ? ToInt(property.Value) : null
        };
        if (attributes.Flags.HasFlag(WiaPropertyFlags.Range))
        {
            return probe with { Minimum = attributes.Min, Maximum = attributes.Max };
        }
        if (attributes.Flags.HasFlag(WiaPropertyFlags.List))
        {
            return probe with
            {
                Values = (attributes.Values ?? []).Select(ToInt).OfType<object>().ToList()
            };
        }
        return probe;
    }

    public NativeWriteResult Write(NativeSettingBinding binding, object nativeValue)
    {
        var property = Find(binding.NativeId);
        if (property == null)
        {
            return new NativeWriteResult(NativeWriteStatus.Unsupported,
                $"The driver does not expose {binding.NativeName}.");
        }
        try
        {
            property.Value = (int) KeyedSettingNegotiator.ToLong(nativeValue);
            return new NativeWriteResult(NativeWriteStatus.Accepted, null);
        }
        catch (Exception e)
        {
            _logger?.LogDebug(e, "Could not write WIA property {PropertyId}", binding.NativeId);
            return new NativeWriteResult(NativeWriteStatus.Failed,
                $"Writing {binding.NativeName} failed: {e.Message}");
        }
    }

    public object? Read(NativeSettingBinding binding) => ToInt(Find(binding.NativeId)?.Value);

    private WiaProperty? Find(int propertyId)
    {
        try
        {
            return _lookup(propertyId);
        }
        catch (Exception e)
        {
            _logger?.LogDebug(e, "Could not enumerate WIA properties while looking for {PropertyId}", propertyId);
            throw new InvalidOperationException($"The WIA properties could not be enumerated: {e.Message}", e);
        }
    }

    private static object? ToInt(object? value) => value switch
    {
        int i => i,
        short or ushort or byte or sbyte or uint or long => (int) KeyedSettingNegotiator.ToLong(value),
        _ => null
    };
}
#endif
