namespace NAPS2.Scan;

/// <summary>
/// Scanning options specific to the WIA driver.
/// </summary>
public class WiaOptions
{
    /// <summary>
    /// Driver-side image and page processing requests. A null member means that no request is sent for that
    /// operation; the driver's existing setting is left unchanged.
    /// </summary>
    public DriverProcessingOptions ProcessingOptions { get; set; } = new();

    public WiaApiVersion WiaApiVersion { get; set; }

    public bool OffsetWidth { get; set; }
}
