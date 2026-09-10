namespace NAPS2.Scan;

/// <summary>
/// Scanning options specific to the WIA driver.
/// </summary>
public class WiaOptions
{
    /// <summary>
    /// Physical feed orientation. Null preserves the driver's configuration and supplied page dimensions.
    /// This does not rotate the output image. Native UI settings take precedence.
    /// </summary>
    public WiaFeedOrientation? FeedOrientation { get; set; }

    /// <summary>
    /// Driver-side image and page processing requests. A null member means that no request is sent for that
    /// operation; the driver's existing setting is left unchanged.
    /// </summary>
    public DriverProcessingOptions ProcessingOptions { get; set; } = new();

    public WiaApiVersion WiaApiVersion { get; set; }

    public bool OffsetWidth { get; set; }
}
