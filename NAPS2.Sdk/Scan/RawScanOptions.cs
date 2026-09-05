using NAPS2.Images;
using NAPS2.Serialization;

namespace NAPS2.Scan;

/// <summary>
/// Options used to acquire scanner data before any image processing is performed.
/// </summary>
/// <remarks>
/// This type deliberately contains acquisition settings only. Output encoding, thumbnails, OCR, blank-page
/// filtering, and other software processing settings belong to the caller's processing pipeline.
/// </remarks>
public sealed class RawScanOptions
{
    /// <summary>
    /// The driver type used for the acquisition. A default value is resolved from the device or platform by the
    /// scanner controller.
    /// </summary>
    public Driver Driver { get; set; }

    /// <summary>
    /// The physical device to acquire from.
    /// </summary>
    public ScanDevice? Device { get; set; }

    /// <summary>
    /// The physical paper source to use.
    /// </summary>
    public PaperSource PaperSource { get; set; }

    /// <summary>
    /// The requested acquisition resolution in dots per inch.
    /// </summary>
    public int Dpi { get; set; }

    /// <summary>
    /// The requested page size or scan area.
    /// </summary>
    public PageSize? PageSize { get; set; }

    /// <summary>
    /// The requested scanner bit depth.
    /// </summary>
    public BitDepth BitDepth { get; set; }

    /// <summary>
    /// The horizontal alignment of the requested scan area.
    /// </summary>
    public HorizontalAlign PageAlign { get; set; }

    /// <summary>
    /// Whether the driver's native configuration UI should be shown.
    /// </summary>
    public bool UseNativeUI { get; set; }

    /// <summary>
    /// The native window that owns a driver UI, when one is shown.
    /// </summary>
    public IntPtr DialogParent { get; set; }

    /// <summary>
    /// Options specific to the WIA driver.
    /// </summary>
    public WiaOptions WiaOptions { get; set; } = new();

    /// <summary>
    /// Options specific to the TWAIN driver.
    /// </summary>
    public TwainOptions TwainOptions { get; set; } = new();

    /// <summary>
    /// Options specific to the SANE driver.
    /// </summary>
    public SaneOptions SaneOptions { get; set; } = new();

    /// <summary>
    /// Options specific to the eSCL driver.
    /// </summary>
    public EsclOptions EsclOptions { get; set; } = new();

    /// <summary>
    /// Creates an independent copy of these acquisition options.
    /// </summary>
    public RawScanOptions Clone()
    {
        // Keep this in step with future backend option additions. The existing SDK serializer also handles the custom
        // option value types (for example PageSize and ScanDevice) and is the cloning mechanism used by ScanOptions.
        return this.ToXml().FromXml<RawScanOptions>();
    }
}
