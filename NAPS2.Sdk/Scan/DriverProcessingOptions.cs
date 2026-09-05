using System.Xml.Serialization;

namespace NAPS2.Scan;

/// <summary>
/// Requests that a scanner driver perform an image or page operation while acquiring a scan.
/// </summary>
/// <remarks>
/// Every property is nullable deliberately. A null value means that the caller has made no request for that
/// operation, which lets a profile leave the device's existing default in place. A non-null false value is an
/// explicit request to turn a boolean operation off.
/// </remarks>
public sealed class DriverProcessingOptions
{
    /// <summary>
    /// A driver-side brightness value. The valid range is reported by <see cref="DriverProcessingCaps.Brightness"/>.
    /// </summary>
    public int? Brightness { get; set; }

    /// <summary>
    /// A driver-side contrast value. The valid range is reported by <see cref="DriverProcessingCaps.Contrast"/>.
    /// </summary>
    public int? Contrast { get; set; }

    /// <summary>
    /// A fixed clockwise rotation in degrees requested from the driver.
    /// </summary>
    public double? RotationDegrees { get; set; }

    /// <summary>
    /// Requests automatic page orientation detection from the driver.
    /// </summary>
    public bool? AutomaticOrientation { get; set; }

    /// <summary>
    /// Requests automatic deskewing from the driver.
    /// </summary>
    public bool? Deskew { get; set; }

    /// <summary>
    /// Requests automatic brightness or exposure selection from the driver.
    /// </summary>
    public bool? AutomaticBrightness { get; set; }

    /// <summary>
    /// Requests automatic page size or boundary detection from the driver.
    /// </summary>
    public bool? AutomaticPageSize { get; set; }

    /// <summary>
    /// Requests automatic border or document boundary detection from the driver.
    /// </summary>
    public bool? AutomaticBorderDetection { get; set; }

    /// <summary>
    /// Requests automatic cropping to the detected document boundary from the driver.
    /// </summary>
    public bool? AutomaticCrop { get; set; }

    /// <summary>
    /// Requests a driver color detection mode. <see cref="DriverColorDetectionMode.Off"/> is an explicit request to
    /// disable automatic color detection.
    /// </summary>
    public DriverColorDetectionMode? AutomaticColorDetection { get; set; }

    /// <summary>
    /// Requests automatic blank page detection or discard behavior from the driver.
    /// </summary>
    public bool? AutomaticBlankPageDetection { get; set; }

    /// <summary>
    /// Gets whether at least one driver-side operation was requested.
    /// </summary>
    [XmlIgnore]
    public bool HasRequests => Brightness.HasValue ||
                               Contrast.HasValue ||
                               RotationDegrees.HasValue ||
                               AutomaticOrientation.HasValue ||
                               Deskew.HasValue ||
                               AutomaticBrightness.HasValue ||
                               AutomaticPageSize.HasValue ||
                               AutomaticBorderDetection.HasValue ||
                               AutomaticCrop.HasValue ||
                               AutomaticColorDetection.HasValue ||
                               AutomaticBlankPageDetection.HasValue;

    /// <summary>
    /// Compatibility alias for callers that use the existing ScanOptions spelling.
    /// </summary>
    [XmlIgnore]
    public bool? AutoDeskew
    {
        get => Deskew;
        set => Deskew = value;
    }

    /// <summary>
    /// Compatibility alias for callers that use the existing ScanOptions spelling.
    /// </summary>
    [XmlIgnore]
    public double? RotateDegrees
    {
        get => RotationDegrees;
        set => RotationDegrees = value;
    }

    /// <summary>
    /// Compatibility alias for profile models that describe the setting as automatic color.
    /// </summary>
    [XmlIgnore]
    public DriverColorDetectionMode? AutomaticColor
    {
        get => AutomaticColorDetection;
        set => AutomaticColorDetection = value;
    }

    /// <summary>
    /// Compatibility alias for profile models that describe blank-page detection as discard.
    /// </summary>
    [XmlIgnore]
    public bool? AutomaticBlankPageDiscard
    {
        get => AutomaticBlankPageDetection;
        set => AutomaticBlankPageDetection = value;
    }

    /// <summary>
    /// Compatibility alias for callers that use the page-sizing wording.
    /// </summary>
    [XmlIgnore]
    public bool? AutomaticPageSizing
    {
        get => AutomaticPageSize;
        set => AutomaticPageSize = value;
    }
}

/// <summary>
/// Color detection modes that can be requested from a scanner driver.
/// </summary>
public enum DriverColorDetectionMode
{
    /// <summary>
    /// Do not ask the driver to detect color automatically.
    /// </summary>
    Off,

    /// <summary>
    /// Let the driver choose the output color mode.
    /// </summary>
    Automatic,

    /// <summary>
    /// Let the driver distinguish color pages from grayscale pages.
    /// </summary>
    ColorOrGrayscale,

    /// <summary>
    /// Let the driver distinguish color pages from black-and-white pages.
    /// </summary>
    ColorOrBlackAndWhite
}
