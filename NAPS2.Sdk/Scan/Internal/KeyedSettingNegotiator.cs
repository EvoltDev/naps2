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
/// A native value that must be written before a binding's own value takes effect, such as selecting a custom preset
/// before writing its amount.
/// </summary>
/// <param name="Binding">The capability or property to write.</param>
/// <param name="Value">The normalized native value.</param>
/// <param name="Optional">
/// Whether the setting may proceed when the driver does not support the prerequisite. A required prerequisite that
/// cannot be written makes the setting fail.
/// </param>
internal sealed record NativePrerequisite(NativeSettingBinding Binding, object Value, bool Optional = false);

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

    public IReadOnlyList<NativePrerequisite> Prerequisites { get; init; } = [];

    /// <summary>
    /// Requires the driver to report exactly <see cref="ValueType"/> for the capability. Vendor bindings set this:
    /// a vendor-defined id is only trusted when its type matches the documented contract.
    /// </summary>
    public bool RequireExactType { get; init; }

    /// <summary>
    /// Other item types accepted under <see cref="RequireExactType"/>, when a device has been observed reporting a
    /// different integer type than the documentation for the same values. The value is written with the type the
    /// device reports.
    /// </summary>
    public IReadOnlyList<NativeValueType> AlternativeTypes { get; init; } = [];

    /// <summary>
    /// Where the binding is documented, for vendor bindings. Reported as the evidence for the setting.
    /// </summary>
    public string? Source { get; init; }

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
/// Negotiates keyed driver settings through protocol bindings: picks the first usable binding for each key, validates
/// the value against what the driver offers, writes in dependency order, and reads every written value back once all
/// writes are done, because a later write can change an earlier one.
/// </summary>
internal static class KeyedSettingNegotiator
{
    private const double RealTolerance = 0.001;

    /// <summary>
    /// Applies the keyed requests in <paramref name="options"/>.
    /// </summary>
    /// <param name="access">Native access for the opened source.</param>
    /// <param name="protocol">The protocol name used in messages.</param>
    /// <param name="candidates">
    /// The bindings for each key in preference order: the standard binding first, then matching vendor bindings.
    /// </param>
    /// <param name="gaps">Why a key has no binding, for keys without candidates.</param>
    /// <param name="options">The requests.</param>
    public static IReadOnlyList<DriverProcessingSetting> Apply(IDriverSettingAccess access, string protocol,
        IReadOnlyDictionary<string, IReadOnlyList<NativeSettingBinding>> candidates,
        IReadOnlyDictionary<string, string> gaps, DriverProcessingOptions? options)
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
            if (!candidates.TryGetValue(keyed.Name, out var bindings) || bindings.Count == 0)
            {
                results.Add(Result(keyed, DriverProcessingStatus.Unsupported,
                    gaps.TryGetValue(keyed.Name, out var gap) ? gap : KeyedDriverSettings.UnboundMessage(protocol)));
                continue;
            }

            var selection = Select(access, bindings, keyed.Request.Value!);
            if (selection.Binding == null)
            {
                results.Add(Result(keyed, selection.Status, selection.Message, selection.LastBinding));
                continue;
            }
            pending.Add((keyed, selection.Binding, selection.Native!));
        }

        var written = new List<(KeyedSettingRequest Request, NativeSettingBinding Binding, object Native,
            NativeWriteStatus Status)>();
        foreach (var (keyed, binding, native) in pending.OrderBy(x => x.Binding.Order))
        {
            // A prerequisite can be destructive on its own (a Kodak blank page mode discards pages with whatever
            // threshold the source already holds), so every prerequisite this setting changed is put back when the
            // setting does not go through. A prerequisite that cannot be put back makes the setting Failed and is
            // named in the message, so the caller cannot mistake the source's state for its defaults.
            var changed = new List<ChangedPrerequisite>();

            void Unsuccessful(DriverProcessingStatus status, string? message)
            {
                var restore = RestorePrerequisites(access, changed);
                if (restore != null)
                {
                    status = DriverProcessingStatus.Failed;
                    message = message == null ? restore : $"{message} {restore}";
                }
                results.Add(Result(keyed, status, message, binding));
            }

            var prerequisite = WritePrerequisites(access, binding, changed);
            if (prerequisite != null)
            {
                Unsuccessful(DriverProcessingStatus.Failed, prerequisite);
                continue;
            }

            // Probe again now: earlier writes and the prerequisites can change access and the offered values.
            var probe = SafeProbe(access, binding);
            switch (probe.State)
            {
                case DriverProcessingCapabilityState.Unsupported:
                    Unsuccessful(DriverProcessingStatus.Unsupported,
                        probe.Message ?? "The driver does not support this setting.");
                    continue;
                case DriverProcessingCapabilityState.ReadOnly:
                    Unsuccessful(DriverProcessingStatus.Rejected, "The driver reports this setting as read-only.");
                    continue;
                case DriverProcessingCapabilityState.QueryFailed:
                    Unsuccessful(DriverProcessingStatus.Failed,
                        probe.Message ?? "The driver could not be queried for this setting.");
                    continue;
            }

            var offered = CheckOffered(binding, native, probe);
            if (offered != null)
            {
                Unsuccessful(DriverProcessingStatus.Rejected, offered);
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
                    Unsuccessful(DriverProcessingStatus.Unsupported, write.Message);
                    break;
                case NativeWriteStatus.Rejected:
                    Unsuccessful(DriverProcessingStatus.Rejected, write.Message);
                    break;
                default:
                    Unsuccessful(DriverProcessingStatus.Failed, write.Message);
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

            var effective = Translate(binding, readBack);
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
    /// Reports support for every key: the first candidate the source supports is probed and translated, keys
    /// without a usable candidate are unsupported with their reason.
    /// </summary>
    public static ImmutableList<DriverSettingCaps> QueryCaps(IDriverSettingAccess access,
        IReadOnlyDictionary<string, IReadOnlyList<NativeSettingBinding>> candidates,
        IReadOnlyDictionary<string, string> gaps)
    {
        var caps = ImmutableList.CreateBuilder<DriverSettingCaps>();
        foreach (var (key, bindings) in candidates.Where(x => x.Value.Count > 0)
                     .OrderBy(x => x.Value.Min(b => b.Order)).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            NativeSettingBinding? chosen = null;
            NativeProbe? probe = null;
            foreach (var binding in bindings)
            {
                chosen = binding;
                probe = SafeProbe(access, binding);
                if (probe.State != DriverProcessingCapabilityState.Unsupported)
                {
                    break;
                }
            }
            caps.Add(new DriverSettingCaps
            {
                Key = key,
                State = probe!.State,
                Binding = chosen!.Label,
                Current = Translate(chosen, probe.Current),
                Values = probe.Values?.Select(x => Translate(chosen, x)).OfType<DriverSettingValue>()
                    .Distinct().ToImmutableList(),
                Minimum = Translate(chosen, probe.Minimum),
                Maximum = Translate(chosen, probe.Maximum),
                Message = probe.Message
            });
        }
        foreach (var gap in gaps.Where(x => !candidates.TryGetValue(x.Key, out var b) || b.Count == 0)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
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

    /// <summary>
    /// Builds the candidate lists for one source: the standard binding for each key, then the vendor bindings whose
    /// identity and driver version match.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<NativeSettingBinding>> Candidates(
        IReadOnlyDictionary<string, NativeSettingBinding> standard, IEnumerable<VendorBindingSet> vendors,
        DriverDeviceIdentity? identity)
    {
        var result = standard.ToDictionary(x => x.Key, x => new List<NativeSettingBinding> { x.Value },
            StringComparer.Ordinal);
        foreach (var vendor in vendors)
        {
            foreach (var binding in vendor.Applicable(identity))
            {
                if (!result.TryGetValue(binding.Key, out var list))
                {
                    result[binding.Key] = list = [];
                }
                list.Add(binding);
            }
        }
        return result.ToDictionary(x => x.Key, x => (IReadOnlyList<NativeSettingBinding>) x.Value,
            StringComparer.Ordinal);
    }

    internal static bool ValuesEqual(DriverSettingValue left, DriverSettingValue right)
    {
        if (left.Kind == DriverSettingValueKind.Real || right.Kind == DriverSettingValueKind.Real)
        {
            return TryGetNumber(left, out var l) && TryGetNumber(right, out var r) && Math.Abs(l - r) < RealTolerance;
        }
        return left.Kind == right.Kind && Equals(left.ToObject(), right.ToObject());
    }

    /// <summary>
    /// Picks the first candidate that can represent the value, that the source does not report as unsupported or
    /// read-only, and that offers the value when it reports what it offers. A standard binding that definitively
    /// cannot take the value therefore falls through to the vendor binding. When no candidate is usable now but one
    /// is currently unsupported, the last such one is still selected: many capabilities are only exposed once a
    /// prerequisite or an earlier setting puts the source in the right mode (a custom preset, a fill color), so
    /// support is decided by the probe after those writes. Access and offered values are checked again at that point.
    /// </summary>
    private static (NativeSettingBinding? Binding, object? Native, DriverProcessingStatus Status, string? Message,
        NativeSettingBinding? LastBinding) Select(IDriverSettingAccess access,
            IReadOnlyList<NativeSettingBinding> bindings, DriverSettingValue value)
    {
        var status = DriverProcessingStatus.Unsupported;
        string? message = null;
        NativeSettingBinding? last = null;
        (NativeSettingBinding Binding, object Native)? deferred = null;
        (string Message, NativeSettingBinding Binding)? unusable = null;
        foreach (var binding in bindings)
        {
            last = binding;
            var conversion = Convert(binding, value);
            if (conversion.Error != null)
            {
                status = DriverProcessingStatus.Rejected;
                message = conversion.Error;
                continue;
            }

            var probe = SafeProbe(access, binding);
            if (probe.State == DriverProcessingCapabilityState.Unsupported)
            {
                deferred = (binding, conversion.Value!);
                continue;
            }
            if (probe.State == DriverProcessingCapabilityState.ReadOnly)
            {
                unusable ??= ("The driver reports this setting as read-only.", binding);
                continue;
            }
            if (CheckOffered(binding, conversion.Value!, probe) is { } notOffered)
            {
                unusable ??= (notOffered, binding);
                continue;
            }
            return (binding, conversion.Value, default, null, binding);
        }
        if (deferred is { } d)
        {
            return (d.Binding, d.Native, default, null, d.Binding);
        }
        return unusable is { } u
            ? (null, null, DriverProcessingStatus.Rejected, u.Message, u.Binding)
            : (null, null, status, message, last);
    }

    private sealed record ChangedPrerequisite(NativeSettingBinding Binding, object? Original);

    /// <summary>
    /// Writes a binding's prerequisites and records in <paramref name="changed"/> each one it changed, with the value
    /// it had before. A prerequisite counts as satisfied only when it reads back the required value: a driver that
    /// accepts a mode with a change (TWRC_CHECKSTATUS) may have picked a mode in which the dependent setting is
    /// ignored. Returns why the setting cannot proceed, or null.
    /// </summary>
    private static string? WritePrerequisites(IDriverSettingAccess access, NativeSettingBinding binding,
        List<ChangedPrerequisite> changed)
    {
        foreach (var prerequisite in binding.Prerequisites)
        {
            var name = prerequisite.Binding.NativeName;
            var probe = SafeProbe(access, prerequisite.Binding);
            if (probe.State is DriverProcessingCapabilityState.Unsupported && prerequisite.Optional)
            {
                continue;
            }
            if (probe.State is not (DriverProcessingCapabilityState.Writable or
                DriverProcessingCapabilityState.Unknown))
            {
                return $"The prerequisite {name} is {probe.State}.";
            }

            var original = probe.Current ?? SafeRead(access, prerequisite.Binding);
            if (original != null && SameNative(original, prerequisite.Value))
            {
                continue;
            }
            if (CheckOffered(prerequisite.Binding, prerequisite.Value, probe) is { } notOffered)
            {
                return $"The prerequisite {name} cannot be set: {notOffered}";
            }

            var write = SafeWrite(access, prerequisite.Binding, prerequisite.Value);
            if (write.Status is not (NativeWriteStatus.Accepted or NativeWriteStatus.AcceptedWithChange))
            {
                if (write.Status == NativeWriteStatus.Unsupported && prerequisite.Optional)
                {
                    continue;
                }
                return $"The prerequisite {name} could not be set: {write.Message}";
            }
            changed.Add(new ChangedPrerequisite(prerequisite.Binding, original));

            var readBack = SafeRead(access, prerequisite.Binding);
            if (readBack == null)
            {
                return $"The prerequisite {name} was accepted, but its value could not be read back.";
            }
            if (!SameNative(readBack, prerequisite.Value))
            {
                return $"The prerequisite {name} reads back {Describe(readBack)} instead of " +
                       $"{Describe(prerequisite.Value)}.";
            }
        }
        return null;
    }

    /// <summary>
    /// Puts back, newest first, the prerequisites a setting changed. Returns which could not be put back, or null.
    /// </summary>
    private static string? RestorePrerequisites(IDriverSettingAccess access, List<ChangedPrerequisite> changed)
    {
        var failures = new List<string>();
        for (var i = changed.Count - 1; i >= 0; i--)
        {
            var (binding, original) = changed[i];
            if (original == null)
            {
                failures.Add($"{binding.NativeName} (its previous value is unknown)");
                continue;
            }
            var write = SafeWrite(access, binding, original);
            var readBack = write.Status is NativeWriteStatus.Accepted or NativeWriteStatus.AcceptedWithChange
                ? SafeRead(access, binding)
                : null;
            if (readBack == null || !SameNative(readBack, original))
            {
                failures.Add($"{binding.NativeName} (still {Describe(readBack ?? "unreadable")})");
            }
        }
        changed.Clear();
        return failures.Count == 0
            ? null
            : $"The prerequisites this setting changed could not be restored: {string.Join(", ", failures)}.";
    }

    private static bool SameNative(object left, object right)
    {
        try
        {
            return Math.Abs(ToDouble(left) - ToDouble(right)) < RealTolerance;
        }
        catch (Exception)
        {
            return Equals(left, right);
        }
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

        if (probe.Values is { Count: > 0 } reals && binding.ValueType == NativeValueType.Fix32)
        {
            var requested = ToDouble(native);
            return reals.Any(x => Math.Abs(ToDouble(x) - requested) < RealTolerance)
                ? null
                : string.Format(CultureInfo.InvariantCulture, "The driver does not offer {0}.",
                    binding.FromNative(native)?.ToString() ?? Describe(native));
        }

        if (probe.Values is { Count: > 0 } values)
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
        Translate(binding, native)?.ToString() ?? native.ToString(CultureInfo.InvariantCulture);

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
            Binding = binding?.Label,
            Evidence = binding?.Source
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
