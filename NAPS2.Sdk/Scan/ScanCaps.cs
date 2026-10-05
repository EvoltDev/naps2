namespace NAPS2.Scan;

/// <summary>
/// Represents scanner capabilities. This includes valid values for scanning options and extra metadata beyond just the
/// device name and id.
/// </summary>
public class ScanCaps
{
    /// <summary>
    /// Driver-side image and page processing capabilities. This is separate from processing requests in
    /// <see cref="TwainOptions.ProcessingOptions"/> and <see cref="WiaOptions.ProcessingOptions"/>.
    /// </summary>
    public DriverProcessingCaps? DriverProcessingCaps { get; init; }

    /// <summary>
    /// Every capability the driver reports, when requested with
    /// <see cref="ScanOptions.IncludeDriverCapabilityInventory"/> and supported by the driver (TWAIN and WIA).
    /// </summary>
    public DriverCapabilityInventory? DriverCapabilityInventory { get; init; }

    /// <summary>
    /// What the driver accepted when this scan's configuration was applied without acquiring, when requested with
    /// <see cref="ScanOptions.ProbeDriverProcessing"/>.
    /// </summary>
    public DriverProcessingResult? DriverProcessingProbe { get; init; }

    /// <summary>
    /// Metadata for the device.
    /// </summary>
    public MetadataCaps? MetadataCaps { get; init; }

    /// <summary>
    /// Valid values for ScanOptions.PaperSource.
    /// </summary>
    public PaperSourceCaps? PaperSourceCaps { get; init; }

    /// <summary>
    /// Capabilities specific to the Flatbed paper source.
    /// </summary>
    public PerSourceCaps? FlatbedCaps { get; init; }

    /// <summary>
    /// Capabilities specific to the Feeder paper source.
    /// </summary>
    public PerSourceCaps? FeederCaps { get; init; }

    /// <summary>
    /// Capabilities specific to the Duplex paper source.
    /// </summary>
    public PerSourceCaps? DuplexCaps { get; init; }
}
