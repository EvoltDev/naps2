using System.Collections.Immutable;
using System.Globalization;
using NAPS2.Scan.Exceptions;

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
/// Negotiates keyed driver settings through protocol bindings: tries each key's bindings in dependency order, the first
/// one the source can take the value through right now first, validates the value against what the driver offers
/// after the prerequisites are written, and reads every written value back once all writes are done, because a later
/// write can change an earlier one.
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
    /// <param name="unrestored">
    /// Receives the native name of every prerequisite that was changed for a setting that did not go through and
    /// could not be put back. Acquisition must not proceed when this is non-empty: the source is left in a state
    /// nobody requested, which can be destructive (a blank page mode discarding pages).
    /// </param>
    public static IReadOnlyList<DriverProcessingSetting> Apply(IDriverSettingAccess access, string protocol,
        IReadOnlyDictionary<string, IReadOnlyList<NativeSettingBinding>> candidates,
        IReadOnlyDictionary<string, string> gaps, DriverProcessingOptions? options,
        ICollection<string>? unrestored = null)
    {
        var results = new List<DriverProcessingSetting>();
        var pending = new List<(KeyedSettingRequest Request, List<(NativeSettingBinding Binding, object Native)>
            Candidates)>();

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

            var ranked = Rank(access, bindings, keyed.Request.Value!, out var conversionError, out var last);
            if (ranked.Count == 0)
            {
                results.Add(Result(keyed, DriverProcessingStatus.Rejected, conversionError, last));
                continue;
            }
            pending.Add((keyed, ranked));
        }

        var written = new List<(KeyedSettingRequest Request, NativeSettingBinding Binding, object Native,
            NativeWriteStatus Status)>();
        foreach (var (keyed, ranked) in pending.OrderBy(x => x.Candidates[0].Binding.Order))
        {
            // Usability is decided here, in dependency order, after the earlier settings and each candidate's own
            // prerequisites are written: either can expose a capability, make it writable, or widen what it offers.
            // A candidate that still cannot take the value falls through to the next one.
            CandidateFailure? reported = null;
            var applied = false;
            foreach (var (binding, native) in ranked)
            {
                var failure = TryCandidate(access, binding, native, unrestored, out var writeStatus);
                if (failure == null)
                {
                    written.Add((keyed, binding, native, writeStatus));
                    applied = true;
                    break;
                }
                if (failure.Unrestored)
                {
                    // The source is in an unrequested state; trying more bindings would only add to it.
                    reported = failure;
                    break;
                }
                if (reported == null ||
                    reported.Status == DriverProcessingStatus.Unsupported &&
                    failure.Status != DriverProcessingStatus.Unsupported)
                {
                    reported = failure;
                }
            }
            if (!applied)
            {
                results.Add(Result(keyed, reported!.Status, reported.Message, reported.Binding));
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

    private sealed record CandidateFailure(DriverProcessingStatus Status, string? Message,
        NativeSettingBinding Binding, bool Unrestored);

    /// <summary>
    /// Orders the candidates that can represent the value: those the source can take it through right now first, in
    /// preference order, then the rest in preference order. Nothing is ruled out here, because a prerequisite or an
    /// earlier setting can still expose a capability, make it writable or change what it offers; the final decision
    /// is made by <see cref="TryCandidate"/> in dependency order.
    /// </summary>
    private static List<(NativeSettingBinding Binding, object Native)> Rank(IDriverSettingAccess access,
        IReadOnlyList<NativeSettingBinding> bindings, DriverSettingValue value, out string? conversionError,
        out NativeSettingBinding? last)
    {
        conversionError = null;
        last = null;
        var usableNow = new List<(NativeSettingBinding, object)>();
        var later = new List<(NativeSettingBinding, object)>();
        foreach (var binding in bindings)
        {
            last = binding;
            var conversion = Convert(binding, value);
            if (conversion.Error != null)
            {
                conversionError = conversion.Error;
                continue;
            }

            var probe = SafeProbe(access, binding);
            var usable = probe.State is DriverProcessingCapabilityState.Writable or
                             DriverProcessingCapabilityState.Unknown &&
                         CheckOffered(binding, conversion.Value!, probe) == null;
            (usable ? usableNow : later).Add((binding, conversion.Value!));
        }
        usableNow.AddRange(later);
        return usableNow;
    }

    /// <summary>
    /// Writes one candidate: its prerequisites, then, after probing again, the value. Returns null when the driver
    /// accepted the value, or why it did not. Every prerequisite the candidate changed is put back when the value does
    /// not go through; one that cannot be put back is added to <paramref name="unrestored"/> and makes the failure
    /// final.
    /// </summary>
    private static CandidateFailure? TryCandidate(IDriverSettingAccess access, NativeSettingBinding binding,
        object native, ICollection<string>? unrestored, out NativeWriteStatus writeStatus)
    {
        writeStatus = default;
        var changed = new List<ChangedPrerequisite>();

        CandidateFailure Unsuccessful(DriverProcessingStatus status, string? message)
        {
            var restore = RestorePrerequisites(access, changed, unrestored);
            return restore == null
                ? new CandidateFailure(status, message, binding, false)
                : new CandidateFailure(DriverProcessingStatus.Failed,
                    message == null ? restore : $"{message} {restore}", binding, true);
        }

        var prerequisite = WritePrerequisites(access, binding, changed);
        if (prerequisite != null)
        {
            return Unsuccessful(DriverProcessingStatus.Failed, prerequisite);
        }

        var probe = SafeProbe(access, binding);
        switch (probe.State)
        {
            case DriverProcessingCapabilityState.Unsupported:
                return Unsuccessful(DriverProcessingStatus.Unsupported,
                    probe.Message ?? "The driver does not support this setting.");
            case DriverProcessingCapabilityState.ReadOnly:
                return Unsuccessful(DriverProcessingStatus.Rejected, "The driver reports this setting as read-only.");
            case DriverProcessingCapabilityState.QueryFailed:
                return Unsuccessful(DriverProcessingStatus.Failed,
                    probe.Message ?? "The driver could not be queried for this setting.");
        }

        var offered = CheckOffered(binding, native, probe);
        if (offered != null)
        {
            return Unsuccessful(DriverProcessingStatus.Rejected, offered);
        }

        var write = SafeWrite(access, binding, native);
        writeStatus = write.Status;
        return write.Status switch
        {
            NativeWriteStatus.Accepted or NativeWriteStatus.AcceptedWithChange => null,
            NativeWriteStatus.Unsupported => Unsuccessful(DriverProcessingStatus.Unsupported, write.Message),
            NativeWriteStatus.Rejected => Unsuccessful(DriverProcessingStatus.Rejected, write.Message),
            _ => Unsuccessful(DriverProcessingStatus.Failed, write.Message)
        };
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
    private static string? RestorePrerequisites(IDriverSettingAccess access, List<ChangedPrerequisite> changed,
        ICollection<string>? unrestored)
    {
        var failures = new List<string>();
        for (var i = changed.Count - 1; i >= 0; i--)
        {
            var (binding, original) = changed[i];
            if (original == null)
            {
                failures.Add($"{binding.NativeName} (its previous value is unknown)");
                unrestored?.Add(binding.NativeName);
                continue;
            }
            var write = SafeWrite(access, binding, original);
            var readBack = write.Status is NativeWriteStatus.Accepted or NativeWriteStatus.AcceptedWithChange
                ? SafeRead(access, binding)
                : null;
            if (readBack == null || !SameNative(readBack, original))
            {
                failures.Add($"{binding.NativeName} (still {Describe(readBack ?? "unreadable")})");
                unrestored?.Add(binding.NativeName);
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

    /// <summary>
    /// Stops an acquisition whose configuration left prerequisites in a state nobody requested. Call after the
    /// configuration result has been reported and before the source is enabled.
    /// </summary>
    internal static void ThrowIfUnrestored(ICollection<string> unrestored)
    {
        if (unrestored.Count > 0)
        {
            throw new DeviceException(
                $"The scanner could not be returned to its previous {string.Join(", ", unrestored.Distinct())} " +
                "setting after a requested setting failed, so the scan was stopped before acquiring any page.");
        }
    }

    internal static long ToLong(object value) =>
        value is bool b ? (b ? 1 : 0) : System.Convert.ToInt64(value, CultureInfo.InvariantCulture);

    internal static double ToDouble(object value) => System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
}
