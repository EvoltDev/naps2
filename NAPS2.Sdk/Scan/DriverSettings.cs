using System.Globalization;

namespace NAPS2.Scan;

/// <summary>
/// The type of a value carried by a keyed driver setting.
/// </summary>
public enum DriverSettingValueKind
{
    Boolean,
    Integer,
    Real,
    Text
}

/// <summary>
/// A typed scalar used by keyed driver setting requests and capability observations.
/// </summary>
/// <remarks>
/// TWAIN and WIA values are converted to these four kinds at the backend boundary: TWAIN BOOL to
/// <see cref="DriverSettingValueKind.Boolean"/>, integer item types and enumerations to
/// <see cref="DriverSettingValueKind.Integer"/>, FIX32 to <see cref="DriverSettingValueKind.Real"/>, and strings or
/// structures without a scalar equivalent to <see cref="DriverSettingValueKind.Text"/>. Only the member matching
/// <see cref="Kind"/> is meaningful.
/// </remarks>
public sealed record DriverSettingValue
{
    public DriverSettingValueKind Kind { get; init; }

    public bool? BooleanValue { get; init; }

    public long? IntegerValue { get; init; }

    public double? RealValue { get; init; }

    public string? TextValue { get; init; }

    public static DriverSettingValue FromBoolean(bool value) =>
        new() { Kind = DriverSettingValueKind.Boolean, BooleanValue = value };

    public static DriverSettingValue FromInteger(long value) =>
        new() { Kind = DriverSettingValueKind.Integer, IntegerValue = value };

    public static DriverSettingValue FromReal(double value) =>
        new() { Kind = DriverSettingValueKind.Real, RealValue = value };

    public static DriverSettingValue FromText(string value) =>
        new() { Kind = DriverSettingValueKind.Text, TextValue = value ?? throw new ArgumentNullException(nameof(value)) };

    /// <summary>
    /// Gets whether the member selected by <see cref="Kind"/> holds a value.
    /// </summary>
    public bool HasValue => Kind switch
    {
        DriverSettingValueKind.Boolean => BooleanValue.HasValue,
        DriverSettingValueKind.Integer => IntegerValue.HasValue,
        DriverSettingValueKind.Real => RealValue.HasValue,
        DriverSettingValueKind.Text => TextValue != null,
        _ => false
    };

    /// <summary>
    /// Returns the value as a CLR scalar, or null when the selected member is empty.
    /// </summary>
    public object? ToObject() => Kind switch
    {
        DriverSettingValueKind.Boolean => BooleanValue,
        DriverSettingValueKind.Integer => IntegerValue,
        DriverSettingValueKind.Real => RealValue,
        DriverSettingValueKind.Text => TextValue,
        _ => null
    };

    public override string ToString() =>
        Convert.ToString(ToObject(), CultureInfo.InvariantCulture) ?? "";
}

/// <summary>
/// Requests a driver setting by a backend-neutral key.
/// </summary>
/// <remarks>
/// Keys name the desired result (for example a gamma value), not a TWAIN capability or WIA property. Each backend
/// translates a key through its own bindings; a key without a binding for the selected backend is reported as
/// <see cref="DriverProcessingStatus.Unsupported"/> rather than ignored. The operations that already have typed
/// members on <see cref="DriverProcessingOptions"/> must be requested through those members.
/// </remarks>
public sealed record DriverSettingRequest
{
    public string Key { get; init; } = "";

    public DriverSettingValue? Value { get; init; }
}

/// <summary>
/// Where a driver-side operation was executed, when that can be established.
/// </summary>
/// <remarks>
/// A driver accepting or reading back a setting does not prove that the scanner hardware performed the operation;
/// the driver may run it in vendor software on the computer. Unless a binding carries explicit evidence, the location
/// stays <see cref="Unknown"/>.
/// </remarks>
public enum DriverExecutionLocation
{
    Unknown,
    ScannerHardware,
    VendorSoftware,
    Mixed
}
