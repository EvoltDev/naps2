using System.Threading;
using System.Xml.Serialization;
using NAPS2.Images;

namespace NAPS2.Scan;

/// <summary>
/// Receives acquisition configuration and committed raw scanner artifacts.
/// </summary>
/// <remarks>
/// Both methods are synchronous on purpose. Driver callbacks often provide a borrowed native buffer whose lifetime
/// ends when the callback returns. Implementations must copy the data before <see cref="IRawScanArtifactWriter.Write"/>
/// returns and may then queue the owned copy for asynchronous durable writing.
/// </remarks>
public interface IRawScanSink
{
    /// <summary>
    /// Reports the settings that the driver applied or could verify for this acquisition session.
    /// </summary>
    void ConfigurationApplied(DriverProcessingResult result);

    /// <summary>
    /// Starts writing one transfer or container. The returned writer owns copies of all data passed to it.
    /// </summary>
    IRawScanArtifactWriter BeginArtifact(RawScanArtifactHeader header);
}

/// <summary>
/// Accepts borrowed scanner transfer buffers and commits one immutable artifact.
/// </summary>
public interface IRawScanArtifactWriter : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Copies the supplied borrowed buffer before returning. The caller may reuse or release the buffer immediately
    /// after this method returns. Implementations may apply byte based backpressure while making the copy.
    /// </summary>
    void Write(ReadOnlySpan<byte> data, RawBlockLayout? layout = null);

    /// <summary>
    /// Waits for all accepted blocks to be durably written and commits the artifact metadata.
    /// </summary>
    Task CompleteAsync(RawScanArtifactMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts the artifact and releases any queued buffers.
    /// </summary>
    Task AbortAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes the kind of payload being written to a raw artifact.
/// </summary>
public enum RawScanArtifactType
{
    Unknown,
    EncodedImage,
    EncodedContainer,
    NativeBitmap,
    RasterBlock,
    AppleBand,
    SaneFrame
}

/// <summary>
/// Identifies the channel or frame represented by a raster block.
/// </summary>
public enum RawScanFrameType
{
    Unknown,
    Image,
    Gray,
    Red,
    Green,
    Blue,
    Alpha
}

/// <summary>
/// Identifies the physical side of a scanned sheet when the driver can provide it.
/// </summary>
public enum RawScanPageSide
{
    Unknown,
    Front,
    Back
}

/// <summary>
/// Describes the payload and expected representation at the start of one acquisition artifact.
/// </summary>
public sealed record RawScanArtifactHeader
{
    public RawScanArtifactType Type { get; init; }

    /// <summary>
    /// Alias for <see cref="Type"/> for callers that refer to the payload as a kind.
    /// </summary>
    public RawScanArtifactType Kind
    {
        get => Type;
        init => Type = value;
    }

    /// <summary>
    /// The image format when the payload is an encoded image or container.
    /// </summary>
    public ImageFileFormat ImageFormat { get; init; }

    /// <summary>
    /// The MIME content type supplied by a network or driver backend, when available.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The preferred extension for the payload, including the leading period.
    /// </summary>
    public string? FileExtension { get; init; }

    public ImagePixelFormat PixelFormat { get; init; }

    public SubPixelType? SubPixelType { get; init; }

    public RawScanFrameType FrameType { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? Stride { get; init; }

    public double? HorizontalResolution { get; init; }

    public double? VerticalResolution { get; init; }

    /// <summary>
    /// The number of logical pages the driver expects this transfer to contain, when known.
    /// </summary>
    public int? PageCount { get; init; }

    public string? SourceId { get; init; }
}

/// <summary>
/// Describes the layout of one copied raster block within an artifact.
/// </summary>
public sealed record RawBlockLayout
{
    /// <summary>
    /// Offset of the block in the artifact payload, if known at write time.
    /// </summary>
    public long? Offset { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? Stride { get; init; }

    public int? BitsPerPixel { get; init; }

    public int? BytesPerPixel { get; init; }

    public ImagePixelFormat PixelFormat { get; init; }

    public SubPixelType? SubPixelType { get; init; }

    public RawScanFrameType FrameType { get; init; }

    public int? ChannelIndex { get; init; }

    public int? ChannelCount { get; init; }

    public int? PageIndex { get; init; }

    public int? FrameIndex { get; init; }

    public bool IsLastBlock { get; init; }
}

/// <summary>
/// Metadata captured when a raw artifact has finished writing.
/// </summary>
public sealed record RawScanArtifactMetadata
{
    public long? ByteLength { get; init; }

    public string? Sha256 { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? HorizontalResolution { get; init; }

    public double? VerticalResolution { get; init; }

    public ImagePixelFormat PixelFormat { get; init; }

    public SubPixelType? SubPixelType { get; init; }

    public RawScanFrameType FrameType { get; init; }

    public ImageFileFormat ImageFormat { get; init; }

    public string? ContentType { get; init; }

    public int? PageCount { get; init; }

    public int? FrameCount { get; init; }

    public RawScanPageSide PageSide { get; init; }

    public bool? IsDuplex { get; init; }

    public string? DeviceId { get; init; }

    public string? SourceId { get; init; }

    /// <summary>
    /// Driver supplied values that are not represented by the common metadata fields.
    /// </summary>
    public Dictionary<string, string?>? AdditionalMetadata { get; init; }
}

/// <summary>
/// The outcome of applying driver side processing settings for one acquisition session.
/// </summary>
public enum DriverProcessingStatus
{
    Unknown,
    Applied,
    NotRequested,
    Neutralized,
    Unsupported,
    Rejected,
    Failed
}

/// <summary>
/// A single named driver processing setting and its requested and verified values.
/// </summary>
public sealed record DriverProcessingSetting
{
    public string Name { get; init; } = "";

    public DriverProcessingStatus Status { get; init; }

    public object? RequestedValue { get; init; }

    public object? EffectiveValue { get; init; }

    public string? Message { get; init; }
}

/// <summary>
/// Contains the driver processing settings requested, verified and rejected during configuration.
/// </summary>
public sealed record DriverProcessingResult
{
    /// <summary>
    /// Settings in their requested form. Values are backend-neutral scalar values where possible.
    /// </summary>
    public Dictionary<string, object?> RequestedSettingsData { get; set; } = new();

    /// <summary>
    /// Read-only view of the requested settings. The custom XML serializer persists
    /// <see cref="RequestedSettingsData"/> because it requires a concrete dictionary type.
    /// </summary>
    [XmlIgnore]
    public IReadOnlyDictionary<string, object?> RequestedSettings
    {
        get => RequestedSettingsData;
        init => RequestedSettingsData = value == null
            ? new Dictionary<string, object?>()
            : CopySettings(value);
    }

    /// <summary>
    /// Settings read back from the driver after configuration.
    /// </summary>
    public Dictionary<string, object?> EffectiveSettingsData { get; set; } = new();

    /// <summary>
    /// Read-only view of the effective settings. The custom XML serializer persists
    /// <see cref="EffectiveSettingsData"/> because it requires a concrete dictionary type.
    /// </summary>
    [XmlIgnore]
    public IReadOnlyDictionary<string, object?> EffectiveSettings
    {
        get => EffectiveSettingsData;
        init => EffectiveSettingsData = value == null
            ? new Dictionary<string, object?>()
            : CopySettings(value);
    }

    public List<DriverProcessingSetting> SettingsData { get; set; } = [];

    [XmlIgnore]
    public IReadOnlyList<DriverProcessingSetting> Settings
    {
        get => SettingsData;
        init => SettingsData = value == null ? [] : [.. value];
    }

    public List<string> RejectedSettingsData { get; set; } = [];

    [XmlIgnore]
    public IReadOnlyList<string> RejectedSettings
    {
        get => RejectedSettingsData;
        init => RejectedSettingsData = value == null ? [] : [.. value];
    }

    public List<string> UnsupportedSettingsData { get; set; } = [];

    [XmlIgnore]
    public IReadOnlyList<string> UnsupportedSettings
    {
        get => UnsupportedSettingsData;
        init => UnsupportedSettingsData = value == null ? [] : [.. value];
    }

    public List<string> NeutralizedSettingsData { get; set; } = [];

    [XmlIgnore]
    public IReadOnlyList<string> NeutralizedSettings
    {
        get => NeutralizedSettingsData;
        init => NeutralizedSettingsData = value == null ? [] : [.. value];
    }

    public List<string> FailedSettingsData { get; set; } = [];

    [XmlIgnore]
    public IReadOnlyList<string> FailedSettings
    {
        get => FailedSettingsData;
        init => FailedSettingsData = value == null ? [] : [.. value];
    }

    public string? FailureReason { get; init; }

    public bool Succeeded => string.IsNullOrEmpty(FailureReason) &&
                             FailedSettings.Count == 0 &&
                             RejectedSettings.Count == 0;

    private static Dictionary<string, object?> CopySettings(IReadOnlyDictionary<string, object?> settings)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var setting in settings)
        {
            copy.Add(setting.Key, setting.Value);
        }
        return copy;
    }
}
