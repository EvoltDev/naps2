using System.Collections.Immutable;
using System.Globalization;

namespace NAPS2.Scan.Internal;

/// <summary>
/// The native representation a binding writes.
/// </summary>
internal enum NativeValueType
{
    Boolean,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Fix32,
    UInt16Array
}

/// <summary>
/// The result of converting a requested value to its native form.
/// </summary>
internal readonly record struct NativeConversion(object? Value, string? Error)
{
    public static NativeConversion Ok(object value) => new(value, null);

    public static NativeConversion Fail(string error) => new(null, error);
}

/// <summary>
/// Binds one <see cref="DriverSettingKeys"/> key to a native capability or property.
/// </summary>
internal sealed record NativeSettingBinding
{
    public required string Key { get; init; }

    public required string Protocol { get; init; }

    public required int NativeId { get; init; }

    public required string NativeName { get; init; }

    public required NativeValueType ValueType { get; init; }

    /// <summary>
    /// Dependency order. Lower values are written first; values that constrain later settings must come earlier.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// The id whose allowed values describe this setting, when it differs from <see cref="NativeId"/> (for example
    /// TWAIN ICAP_SUPPORTEDBARCODETYPES for ICAP_BARCODESEARCHPRIORITIES).
    /// </summary>
    public int? ValuesNativeId { get; init; }

    public required Func<DriverSettingValue, NativeConversion> ToNative { get; init; }

    /// <summary>
    /// Converts a native value back to the key's value, or null when it has no translation.
    /// </summary>
    public required Func<object, DriverSettingValue?> FromNative { get; init; }

    public string Label => string.Format(CultureInfo.InvariantCulture, "{0} {1} (0x{2:X4})", Protocol, NativeName,
        NativeId);
}

/// <summary>
/// What a backend reports about one native capability or property before writing it.
/// </summary>
internal sealed record NativeProbe
{
    public DriverProcessingCapabilityState State { get; init; }

    public object? Current { get; init; }

    /// <summary>
    /// Every value the driver offers, when it reports an enumeration, list or array.
    /// </summary>
    public IReadOnlyList<object>? Values { get; init; }

    public object? Minimum { get; init; }

    public object? Maximum { get; init; }

    public string? Message { get; init; }
}

internal enum NativeWriteStatus
{
    Accepted,

    /// <summary>The driver accepted the request but changed the value (TWAIN TWRC_CHECKSTATUS).</summary>
    AcceptedWithChange,
    Rejected,
    Unsupported,
    Failed
}

internal readonly record struct NativeWriteResult(NativeWriteStatus Status, string? Message);

/// <summary>
/// Native capability or property access for one opened source. Implementations isolate each call; a failure is
/// reported in the result, never thrown.
/// </summary>
internal interface IDriverSettingAccess
{
    NativeProbe Probe(NativeSettingBinding binding);

    NativeWriteResult Write(NativeSettingBinding binding, object nativeValue);

    /// <summary>
    /// Reads the current value after writing, or null when it cannot be read. Array values are returned as
    /// <c>object[]</c>.
    /// </summary>
    object? Read(NativeSettingBinding binding);
}

/// <summary>
/// Negotiates keyed driver settings through protocol bindings: validates, writes in dependency order, and reads every
/// written value back once all writes are done, because a later write can change an earlier one.
/// </summary>
internal static class KeyedSettingNegotiator
{
    private const double RealTolerance = 0.001;

    public static IReadOnlyList<DriverProcessingSetting> Apply(IDriverSettingAccess access, string protocol,
        IReadOnlyDictionary<string, NativeSettingBinding> bindings, IReadOnlyDictionary<string, string> gaps,
        DriverProcessingOptions? options)
    {
        var results = new List<DriverProcessingSetting>();
        var pending = new List<(KeyedSettingRequest Request, NativeSettingBinding Binding, object Native)>();

        foreach (var keyed in KeyedDriverSettings.Read(options))
        {
            if (keyed.Rejection != null)
            {
                results.Add(Result(keyed, DriverProcessingStatus.Rejected, keyed.Rejection));
                continue;
            }
            if (!bindings.TryGetValue(keyed.Name, out var binding))
            {
                results.Add(Result(keyed, DriverProcessingStatus.Unsupported,
                    gaps.TryGetValue(keyed.Name, out var gap) ? gap : KeyedDriverSettings.UnboundMessage(protocol)));
                continue;
            }

            var conversion = Convert(binding, keyed.Request.Value!);
            if (conversion.Error != null)
            {
                results.Add(Result(keyed, DriverProcessingStatus.Rejected, conversion.Error, binding));
                continue;
            }
            pending.Add((keyed, binding, conversion.Value!));
        }

        var written = new List<(KeyedSettingRequest Request, NativeSettingBinding Binding, object Native,
            NativeWriteStatus Status)>();
        foreach (var (keyed, binding, native) in pending.OrderBy(x => x.Binding.Order))
        {
            var probe = SafeProbe(access, binding);
            switch (probe.State)
            {
                case DriverProcessingCapabilityState.Unsupported:
                    results.Add(Result(keyed, DriverProcessingStatus.Unsupported,
                        probe.Message ?? "The driver does not support this setting.", binding));
                    continue;
                case DriverProcessingCapabilityState.ReadOnly:
                    results.Add(Result(keyed, DriverProcessingStatus.Rejected,
                        "The driver reports this setting as read-only.", binding));
                    continue;
                case DriverProcessingCapabilityState.QueryFailed:
                    results.Add(Result(keyed, DriverProcessingStatus.Failed,
                        probe.Message ?? "The driver could not be queried for this setting.", binding));
                    continue;
            }

            var offered = CheckOffered(binding, native, probe);
            if (offered != null)
            {
                results.Add(Result(keyed, DriverProcessingStatus.Rejected, offered, binding));
                continue;
            }

            var write = SafeWrite(access, binding, native);
            switch (write.Status)
            {
                case NativeWriteStatus.Accepted:
                case NativeWriteStatus.AcceptedWithChange:
                    written.Add((keyed, binding, native, write.Status));
                    break;
                case NativeWriteStatus.Unsupported:
                    results.Add(Result(keyed, DriverProcessingStatus.Unsupported, write.Message, binding));
                    break;
                case NativeWriteStatus.Rejected:
                    results.Add(Result(keyed, DriverProcessingStatus.Rejected, write.Message, binding));
                    break;
                default:
                    results.Add(Result(keyed, DriverProcessingStatus.Failed, write.Message, binding));
                    break;
            }
        }

        foreach (var (keyed, binding, native, writeStatus) in written)
        {
            var expected = binding.FromNative(native);
            var readBack = SafeRead(access, binding);
            if (readBack == null)
            {
                results.Add(Result(keyed, DriverProcessingStatus.Unknown,
                    "The driver accepted the setting, but its value could not be read back.", binding));
                continue;
            }

            var effective = binding.FromNative(readBack);
            if (effective != null && expected != null && ValuesEqual(effective, expected))
            {
                results.Add(Result(keyed, DriverProcessingStatus.Applied, null, binding, effective));
                continue;
            }
            results.Add(Result(keyed, DriverProcessingStatus.Adjusted,
                writeStatus == NativeWriteStatus.AcceptedWithChange
                    ? "The driver accepted the setting with a different value."
                    : "The driver reads back a different value after it was set.",
                binding, effective ?? DriverSettingValue.FromText(Describe(readBack))));
        }

        // Report in request order so a caller can match results to its requests.
        var order = KeyedDriverSettings.Read(options).Select((x, i) => (x.Name, i))
            .GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First().i);
        return results.OrderBy(x => order.TryGetValue(x.Name, out var i) ? i : int.MaxValue).ToList();
    }

    /// <summary>
    /// Reports support for every key a protocol knows: bound keys are probed on the source, gaps are reported as
    /// unsupported with their reason.
    /// </summary>
    public static ImmutableList<DriverSettingCaps> QueryCaps(IDriverSettingAccess access,
        IReadOnlyDictionary<string, NativeSettingBinding> bindings, IReadOnlyDictionary<string, string> gaps)
    {
        var caps = ImmutableList.CreateBuilder<DriverSettingCaps>();
        foreach (var binding in bindings.Values.OrderBy(x => x.Order))
        {
            var probe = SafeProbe(access, binding);
            caps.Add(new DriverSettingCaps
            {
                Key = binding.Key,
                State = probe.State,
                Binding = binding.Label,
                Current = Translate(binding, probe.Current),
                Values = probe.Values?.Select(x => Translate(binding, x)).OfType<DriverSettingValue>()
                    .Distinct().ToImmutableList(),
                Minimum = Translate(binding, probe.Minimum),
                Maximum = Translate(binding, probe.Maximum),
                Message = probe.Message
            });
        }
        foreach (var gap in gaps.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            caps.Add(new DriverSettingCaps
            {
                Key = gap.Key,
                State = DriverProcessingCapabilityState.Unsupported,
                Message = gap.Value
            });
        }
        return caps.ToImmutable();
    }

    internal static bool ValuesEqual(DriverSettingValue left, DriverSettingValue right)
    {
        if (left.Kind == DriverSettingValueKind.Real || right.Kind == DriverSettingValueKind.Real)
        {
            return TryGetNumber(left, out var l) && TryGetNumber(right, out var r) && Math.Abs(l - r) < RealTolerance;
        }
        return left.Kind == right.Kind && Equals(left.ToObject(), right.ToObject());
    }

    private static NativeConversion Convert(NativeSettingBinding binding, DriverSettingValue value)
    {
        try
        {
            return binding.ToNative(value);
        }
        catch (Exception e)
        {
            return NativeConversion.Fail($"The value could not be converted: {e.Message}");
        }
    }

    /// <summary>
    /// Rejects a value the driver does not offer, when it reports what it offers. A driver that reports nothing is
    /// left to accept or reject the write itself.
    /// </summary>
    private static string? CheckOffered(NativeSettingBinding binding, object native, NativeProbe probe)
    {
        if (binding.ValueType == NativeValueType.UInt16Array)
        {
            if (probe.Values is not { Count: > 0 })
            {
                return null;
            }
            var offered = new HashSet<long>(probe.Values.Select(ToLong));
            var missing = ((object[]) native).Select(ToLong).Where(x => !offered.Contains(x)).ToList();
            return missing.Count == 0
                ? null
                : $"The driver does not offer {string.Join(", ", missing.Select(x => Describe(binding, x)))}.";
        }

        if (probe.Values is { Count: > 0 } values && binding.ValueType != NativeValueType.Fix32)
        {
            var requested = ToLong(native);
            return values.Any(x => ToLong(x) == requested)
                ? null
                : $"The driver does not offer {Describe(binding, requested)}.";
        }

        if (probe.Minimum != null && probe.Maximum != null)
        {
            var requested = ToDouble(native);
            var minimum = ToDouble(probe.Minimum);
            var maximum = ToDouble(probe.Maximum);
            if (requested < minimum - RealTolerance || requested > maximum + RealTolerance)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "The driver accepts values from {0} to {1}.", minimum, maximum);
            }
        }
        return null;
    }

    private static string Describe(NativeSettingBinding binding, long native) =>
        binding.FromNative(native)?.ToString() ?? native.ToString(CultureInfo.InvariantCulture);

    private static string Describe(object value) => value is object[] array
        ? string.Join(",", array.Select(x => System.Convert.ToString(x, CultureInfo.InvariantCulture)))
        : System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static DriverSettingValue? Translate(NativeSettingBinding binding, object? native)
    {
        if (native == null) return null;
        try
        {
            return binding.FromNative(native);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static NativeProbe SafeProbe(IDriverSettingAccess access, NativeSettingBinding binding)
    {
        try
        {
            return access.Probe(binding);
        }
        catch (Exception e)
        {
            return new NativeProbe { State = DriverProcessingCapabilityState.QueryFailed, Message = e.Message };
        }
    }

    private static NativeWriteResult SafeWrite(IDriverSettingAccess access, NativeSettingBinding binding,
        object native)
    {
        try
        {
            return access.Write(binding, native);
        }
        catch (Exception e)
        {
            return new NativeWriteResult(NativeWriteStatus.Failed, e.Message);
        }
    }

    private static object? SafeRead(IDriverSettingAccess access, NativeSettingBinding binding)
    {
        try
        {
            return access.Read(binding);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DriverProcessingSetting Result(KeyedSettingRequest keyed, DriverProcessingStatus status,
        string? message, NativeSettingBinding? binding = null, DriverSettingValue? effective = null) =>
        new()
        {
            Name = keyed.Name,
            Status = status,
            RequestedValue = keyed.RequestedValue,
            EffectiveValue = effective?.ToObject(),
            Message = message,
            Binding = binding?.Label
        };

    private static bool TryGetNumber(DriverSettingValue value, out double number)
    {
        switch (value.Kind)
        {
            case DriverSettingValueKind.Real when value.RealValue.HasValue:
                number = value.RealValue.Value;
                return true;
            case DriverSettingValueKind.Integer when value.IntegerValue.HasValue:
                number = value.IntegerValue.Value;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    internal static long ToLong(object value) =>
        value is bool b ? (b ? 1 : 0) : System.Convert.ToInt64(value, CultureInfo.InvariantCulture);

    internal static double ToDouble(object value) => System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
}
