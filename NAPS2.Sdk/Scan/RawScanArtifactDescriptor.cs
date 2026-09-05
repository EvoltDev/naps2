using NAPS2.Images;

namespace NAPS2.Scan;

/// <summary>
/// Immutable locations and metadata for a committed raw acquisition artifact.
/// </summary>
public sealed record RawScanArtifactDescriptor
{
    public RawScanArtifactDescriptor()
    {
    }

    public RawScanArtifactDescriptor(string payloadPath, RawScanArtifactHeader header,
        RawScanArtifactMetadata metadata)
    {
        PayloadPath = payloadPath;
        Header = header;
        Metadata = metadata;
    }

    /// <summary>
    /// The durable payload path. The path must refer to an immutable, committed artifact before being exposed.
    /// </summary>
    public string PayloadPath { get; init; } = "";

    /// <summary>
    /// The optional manifest path associated with the payload.
    /// </summary>
    public string? ManifestPath { get; init; }

    public RawScanArtifactHeader Header { get; init; } = new();

    public RawScanArtifactMetadata Metadata { get; init; } = new();

    public Guid? ArtifactId { get; init; }

    public long? ByteLength { get; init; }

    public string? Sha256 { get; init; }

    public bool IsCommitted { get; init; }

    /// <summary>
    /// Ordered transport blocks that make up the payload. Raster artifacts use these records to reconstruct logical
    /// strips after a bounded writer has split a borrowed scanner buffer into multiple durable writes.
    /// </summary>
    public List<RawScanArtifactBlock> Blocks { get; init; } = [];

    public List<RawScanPageMetadata> Pages { get; init; } = [];
}

/// <summary>
/// One ordered payload block in a raw artifact.
/// </summary>
/// <remarks>
/// <see cref="Layout"/> may be omitted on a continuation block when a transport protocol carries the layout only on
/// the first chunk of a logical strip. The block offset and length always describe the durable payload range.
/// </remarks>
public sealed record RawScanArtifactBlock
{
    public long Offset { get; init; }

    public int Length { get; init; }

    public RawBlockLayout? Layout { get; init; }
}

/// <summary>
/// Metadata for one logical page discovered inside a raw artifact.
/// </summary>
public sealed record RawScanPageMetadata
{
    public int PageIndex { get; init; }

    public RawScanPageSide Side { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? HorizontalResolution { get; init; }

    public double? VerticalResolution { get; init; }

    public ImagePixelFormat PixelFormat { get; init; }

    public long? Offset { get; init; }

    public long? Length { get; init; }

    public string? SourceId { get; init; }
}
