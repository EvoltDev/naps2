using System.Collections.Immutable;

namespace NAPS2.Scan;

/// <summary>
/// Describes the state of one driver-side processing capability.
/// </summary>
public enum DriverProcessingCapabilityState
{
    /// <summary>
    /// The capability was not queried or the backend did not provide a state.
    /// </summary>
    Unknown,

    /// <summary>
    /// The backend reports that the operation is unavailable.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The operation is available and can be read, but the driver does not allow it to be changed.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// The operation is available and can be read and changed for a scan.
    /// </summary>
    Writable,

    /// <summary>
    /// The backend could not query the operation reliably.
    /// </summary>
    QueryFailed
}

/// <summary>
/// A numeric driver-side capability such as brightness, contrast, or rotation.
/// </summary>
public sealed class DriverProcessingNumericCaps
{
    /// <summary>
    /// Describes whether the numeric value is available and writable.
    /// </summary>
    public DriverProcessingCapabilityState State { get; init; }

    /// <summary>
    /// The lowest accepted value, when the driver reports a range.
    /// </summary>
    public double? Minimum { get; init; }

    /// <summary>
    /// The highest accepted value, when the driver reports a range.
    /// </summary>
    public double? Maximum { get; init; }

    /// <summary>
    /// The smallest supported increment, when the driver reports one.
    /// </summary>
    public double? Step { get; init; }

    /// <summary>
    /// The value currently read from the driver.
    /// </summary>
    public double? Current { get; init; }

    /// <summary>
    /// The driver's default value, when it is exposed.
    /// </summary>
    public double? Default { get; init; }

    /// <summary>
    /// Gets whether the driver reports this capability as available.
    /// </summary>
    public bool IsSupported => State is DriverProcessingCapabilityState.ReadOnly or
        DriverProcessingCapabilityState.Writable;

    /// <summary>
    /// Gets whether a value can be requested from the driver.
    /// </summary>
    public bool IsWritable => State == DriverProcessingCapabilityState.Writable;
}

/// <summary>
/// A boolean driver-side capability such as automatic orientation or deskew.
/// </summary>
public sealed class DriverProcessingBooleanCaps
{
    /// <summary>
    /// Describes whether the boolean value is available and writable.
    /// </summary>
    public DriverProcessingCapabilityState State { get; init; }

    /// <summary>
    /// The value currently read from the driver.
    /// </summary>
    public bool? Current { get; init; }

    /// <summary>
    /// The driver's default value, when it is exposed.
    /// </summary>
    public bool? Default { get; init; }

    /// <summary>
    /// Gets whether the driver reports this capability as available.
    /// </summary>
    public bool IsSupported => State is DriverProcessingCapabilityState.ReadOnly or
        DriverProcessingCapabilityState.Writable;

    /// <summary>
    /// Gets whether a value can be requested from the driver.
    /// </summary>
    public bool IsWritable => State == DriverProcessingCapabilityState.Writable;
}

/// <summary>
/// A driver-side color detection capability with its supported modes and current/default mode.
/// </summary>
public sealed class DriverProcessingColorCaps
{
    /// <summary>
    /// Describes whether color detection is available and writable.
    /// </summary>
    public DriverProcessingCapabilityState State { get; init; }

    /// <summary>
    /// Color detection modes reported by the driver.
    /// </summary>
    public ImmutableList<DriverColorDetectionMode>? SupportedModes { get; init; }

    /// <summary>
    /// The mode currently read from the driver.
    /// </summary>
    public DriverColorDetectionMode? Current { get; init; }

    /// <summary>
    /// The driver's default mode, when it is exposed.
    /// </summary>
    public DriverColorDetectionMode? Default { get; init; }

    /// <summary>
    /// Gets whether the driver reports this capability as available.
    /// </summary>
    public bool IsSupported => State is DriverProcessingCapabilityState.ReadOnly or
        DriverProcessingCapabilityState.Writable;

    /// <summary>
    /// Gets whether a mode can be requested from the driver.
    /// </summary>
    public bool IsWritable => State == DriverProcessingCapabilityState.Writable;
}

/// <summary>
/// Driver-side image and page processing capabilities returned with <see cref="ScanCaps"/>.
/// </summary>
/// <remarks>
/// Capability values are observations from a device. They are kept separate from
/// <see cref="DriverProcessingOptions"/>, which contains requests for a future acquisition. A capability may be
/// read-only even when its current value is useful to the caller, and a query failure is kept distinct from an
/// unsupported operation so the UI can decide whether to retry or use a software fallback.
/// </remarks>
public sealed class DriverProcessingCaps
{
    public DriverProcessingNumericCaps? Brightness { get; init; }

    public DriverProcessingNumericCaps? Contrast { get; init; }

    public DriverProcessingNumericCaps? RotationDegrees { get; init; }

    public DriverProcessingBooleanCaps? AutomaticOrientation { get; init; }

    public DriverProcessingBooleanCaps? Deskew { get; init; }

    public DriverProcessingBooleanCaps? AutomaticBrightness { get; init; }

    public DriverProcessingBooleanCaps? AutomaticPageSize { get; init; }

    public DriverProcessingBooleanCaps? AutomaticBorderDetection { get; init; }

    public DriverProcessingBooleanCaps? AutomaticCrop { get; init; }

    public DriverProcessingColorCaps? AutomaticColorDetection { get; init; }

    public DriverProcessingBooleanCaps? AutomaticBlankPageDetection { get; init; }
}
