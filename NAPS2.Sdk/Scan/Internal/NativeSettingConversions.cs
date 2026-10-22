using System.Globalization;

namespace NAPS2.Scan.Internal;

/// <summary>
/// Value conversions shared by the TWAIN and WIA setting bindings. Native values are normalized: booleans as
/// <see cref="bool"/>, FIX32 as <see cref="double"/>, integers as <see cref="int"/>, arrays as <c>object[]</c> of
/// <see cref="ushort"/> (TWAIN) or <see cref="int"/> (WIA vectors).
/// </summary>
internal static class NativeSettingConversions
{
    public static Func<DriverSettingValue, NativeConversion> BooleanToNative() => value =>
        value is { Kind: DriverSettingValueKind.Boolean, BooleanValue: { } b }
            ? NativeConversion.Ok(b)
            : NativeConversion.Fail("A boolean value is required.");

    public static DriverSettingValue? BooleanFromNative(object native) =>
        DriverSettingValue.FromBoolean(KeyedSettingNegotiator.ToLong(native) != 0);

    /// <summary>
    /// A boolean stored as two integer values, as WIA properties do.
    /// </summary>
    public static Func<DriverSettingValue, NativeConversion> BooleanToInteger(int trueValue, int falseValue) =>
        value => value is { Kind: DriverSettingValueKind.Boolean, BooleanValue: { } b }
            ? NativeConversion.Ok(b ? trueValue : falseValue)
            : NativeConversion.Fail("A boolean value is required.");

    public static Func<object, DriverSettingValue?> BooleanFromInteger(int trueValue, int falseValue) => native =>
    {
        var raw = KeyedSettingNegotiator.ToLong(native);
        return raw == trueValue ? DriverSettingValue.FromBoolean(true)
            : raw == falseValue ? DriverSettingValue.FromBoolean(false)
            : null;
    };

    public static Func<DriverSettingValue, NativeConversion> IntegerToNative(int minimum, int maximum) => value =>
    {
        if (value is not { Kind: DriverSettingValueKind.Integer, IntegerValue: { } i })
        {
            return NativeConversion.Fail("An integer value is required.");
        }
        return i < minimum || i > maximum
            ? NativeConversion.Fail(string.Format(CultureInfo.InvariantCulture,
                "The value must be between {0} and {1}.", minimum, maximum))
            : NativeConversion.Ok((int) i);
    };

    public static DriverSettingValue? IntegerFromNative(object native) =>
        DriverSettingValue.FromInteger(KeyedSettingNegotiator.ToLong(native));

    /// <summary>
    /// An integer request written to a FIX32 capability, such as a 0-255 threshold.
    /// </summary>
    public static Func<DriverSettingValue, NativeConversion> IntegerToReal(int minimum, int maximum) => value =>
    {
        var integer = IntegerToNative(minimum, maximum)(value);
        return integer.Error != null ? integer : NativeConversion.Ok((double) (int) integer.Value!);
    };

    public static DriverSettingValue? IntegerFromReal(object native) =>
        DriverSettingValue.FromInteger((long) Math.Round(KeyedSettingNegotiator.ToDouble(native)));

    public static Func<DriverSettingValue, NativeConversion> PositiveRealToNative() => value =>
    {
        double? number = value switch
        {
            { Kind: DriverSettingValueKind.Real, RealValue: { } r } => r,
            { Kind: DriverSettingValueKind.Integer, IntegerValue: { } i } => i,
            _ => null
        };
        return number is > 0 and < 32768
            ? NativeConversion.Ok(number.Value)
            : NativeConversion.Fail("A positive number below 32768 is required.");
    };

    public static DriverSettingValue? RealFromNative(object native) =>
        DriverSettingValue.FromReal(KeyedSettingNegotiator.ToDouble(native));

    /// <summary>
    /// A text value chosen from named native constants.
    /// </summary>
    public static Func<DriverSettingValue, NativeConversion> NamedToNative(IReadOnlyDictionary<string, int> names) =>
        value =>
        {
            if (value is not { Kind: DriverSettingValueKind.Text, TextValue: { } text })
            {
                return NativeConversion.Fail("A text value is required.");
            }
            return names.TryGetValue(text.Trim(), out var native)
                ? NativeConversion.Ok(native)
                : NativeConversion.Fail($"'{text}' is not one of: {string.Join(", ", names.Keys)}.");
        };

    public static Func<object, DriverSettingValue?> NamedFromNative(IReadOnlyDictionary<string, int> names)
    {
        var reverse = names.GroupBy(x => x.Value).ToDictionary(x => x.Key, x => x.First().Key);
        return native => reverse.TryGetValue((int) KeyedSettingNegotiator.ToLong(native), out var name)
            ? DriverSettingValue.FromText(name)
            : null;
    }

    /// <summary>
    /// Named levels spread evenly over the scale the driver reports: the first name is the lowest value it offers, the
    /// last the highest, and any between are equally spaced (rounded). A driver that lists its values instead of a
    /// range is mapped onto the sorted list the same way. Read back, the value is the native integer.
    /// </summary>
    public static Func<DriverSettingValue, NativeProbe, NativeConversion> NamedLevelsInRange(
        IReadOnlyList<string> levels) => (value, probe) =>
    {
        if (value is not { Kind: DriverSettingValueKind.Text, TextValue: { } text })
        {
            return NativeConversion.Fail("A text value is required.");
        }
        var index = levels.ToList().IndexOf(text.Trim());
        if (index < 0)
        {
            return NativeConversion.Fail($"'{text}' is not one of: {string.Join(", ", levels)}.");
        }
        var fraction = levels.Count == 1 ? 1d : (double) index / (levels.Count - 1);
        if (probe.Values is { Count: > 0 } listed)
        {
            var sorted = listed.Select(KeyedSettingNegotiator.ToLong).Distinct().OrderBy(x => x).ToList();
            return NativeConversion.Ok((int) sorted[(int) Math.Round(fraction * (sorted.Count - 1))]);
        }
        if (probe.Minimum == null || probe.Maximum == null)
        {
            return NativeConversion.Fail("The driver reports no range to place the level on.");
        }
        var minimum = KeyedSettingNegotiator.ToLong(probe.Minimum);
        var maximum = KeyedSettingNegotiator.ToLong(probe.Maximum);
        return NativeConversion.Ok((int) Math.Round(minimum + fraction * (maximum - minimum),
            MidpointRounding.AwayFromZero));
    };

    /// <summary>
    /// A comma-separated list of names written as an ordered native array.
    /// </summary>
    public static Func<DriverSettingValue, NativeConversion> NamedListToNative(
        IReadOnlyDictionary<string, int> names, bool int32 = false) => value =>
    {
        if (value is not { Kind: DriverSettingValueKind.Text, TextValue: { } text })
        {
            return NativeConversion.Fail("A comma-separated text value is required.");
        }
        var items = text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (items.Count == 0)
        {
            return NativeConversion.Fail("At least one value is required.");
        }
        var unknown = items.Where(x => !names.ContainsKey(x)).ToList();
        if (unknown.Count > 0)
        {
            return NativeConversion.Fail($"'{string.Join(", ", unknown)}' is not one of: " +
                                         $"{string.Join(", ", names.Keys)}.");
        }
        if (items.Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            return NativeConversion.Fail("A value is listed more than once.");
        }
        return NativeConversion.Ok(int32
            ? items.Select(x => (object) names[x]).ToArray()
            : items.Select(x => (object) (ushort) names[x]).ToArray());
    };

    /// <summary>
    /// Converts a native array, or a single native value, back to the comma-separated names. Returns null when any
    /// item has no name, so an untranslatable value is never presented as a partial list.
    /// </summary>
    public static Func<object, DriverSettingValue?> NamedListFromNative(IReadOnlyDictionary<string, int> names)
    {
        var single = NamedFromNative(names);
        return native =>
        {
            var items = native is object[] array ? array : [native];
            var translated = items.Select(single).ToList();
            return translated.Any(x => x == null)
                ? null
                : DriverSettingValue.FromText(string.Join(",", translated.Select(x => x!.TextValue)));
        };
    }
}
