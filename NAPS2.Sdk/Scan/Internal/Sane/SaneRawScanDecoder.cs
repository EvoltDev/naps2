using System.Globalization;
using System.Threading;
using NAPS2.Images;
using NAPS2.Images.Bitwise;
using NAPS2.Images.Transforms;

namespace NAPS2.Scan;

/// <summary>
/// Decodes raw artifacts produced by the SANE driver.
/// </summary>
/// <remarks>
/// SANE may expose RGB as one interleaved frame or as three consecutive planar frames. The raw driver stores all
/// planar frames in one artifact and records their offsets in artifact metadata. This decoder combines those frames
/// into one logical page after acquisition has completed.
/// </remarks>
public sealed class SaneRawScanDecoder : RawScanDecoder
{
    internal const string FrameCountKey = "sane.frame-count";
    internal const string PlanarKey = "sane.planar";
    internal const string FramePrefix = "sane.frame.";

    public SaneRawScanDecoder(ImageContext imageContext) : base(imageContext)
    {
    }

    public override Task<IReadOnlyList<RawScanPageMetadata>> EnumeratePagesAsync(
        RawScanArtifactDescriptor artifact,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = GetFrameDescriptors(artifact).FirstOrDefault();
        var width = descriptor?.Width ?? artifact.Metadata.Width ?? artifact.Header.Width;
        var height = descriptor?.Height ?? artifact.Metadata.Height ?? artifact.Header.Height;
        return Task.FromResult<IReadOnlyList<RawScanPageMetadata>>(
            new[]
            {
                new RawScanPageMetadata
                {
                    PageIndex = 0,
                    Side = artifact.Metadata.PageSide,
                    Width = width,
                    Height = height,
                    HorizontalResolution = artifact.Metadata.HorizontalResolution ??
                                            artifact.Header.HorizontalResolution,
                    VerticalResolution = artifact.Metadata.VerticalResolution ??
                                          artifact.Header.VerticalResolution,
                    PixelFormat = GetPixelFormat(artifact, descriptor),
                    SourceId = artifact.Metadata.SourceId ?? artifact.Header.SourceId
                }
            });
    }

    public override async Task<IMemoryImage> DecodeAsync(
        RawScanArtifactDescriptor artifact,
        int pageIndex = 0,
        int? maximumEdgeLength = null,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);
        if (pageIndex != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex),
                "A SANE raw artifact contains one logical page.");
        }
        if (maximumEdgeLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdgeLength));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // The decoder is on the final processing path, after the bounded acquisition writer has committed the
        // artifact. Reading it in one array keeps the driver callback free of image construction and lets the
        // bitwise implementation handle row padding consistently.
        var payload = await Task.Run(() => File.ReadAllBytes(artifact.PayloadPath), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var frames = GetFrameDescriptors(artifact);
        if (frames.Count == 0)
        {
            throw new InvalidDataException("The SANE artifact does not contain frame metadata.");
        }

        var planar = IsPlanar(artifact, frames);
        IMemoryImage? image = null;
        try
        {
            image = planar
                ? DecodePlanar(payload, artifact, frames)
                : DecodeInterleaved(payload, artifact, frames[0]);

            cancellationToken.ThrowIfCancellationRequested();
            image.SetResolution(
                (float) (artifact.Metadata.HorizontalResolution ?? artifact.Header.HorizontalResolution ?? 0),
                (float) (artifact.Metadata.VerticalResolution ?? artifact.Header.VerticalResolution ?? 0));

            if (maximumEdgeLength is not { } maxEdge || Math.Max(image.Width, image.Height) <= maxEdge)
            {
                var result = image;
                image = null;
                return result;
            }

            var scale = maxEdge / (double) Math.Max(image.Width, image.Height);
            var transformed = ImageContext.PerformTransform(image, new ScaleTransform(scale));
            image = null;
            return transformed;
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    private IMemoryImage DecodeInterleaved(byte[] payload, RawScanArtifactDescriptor artifact,
        SaneRawFrameDescriptor descriptor)
    {
        var width = descriptor.Width ?? artifact.Metadata.Width ?? artifact.Header.Width;
        var height = descriptor.Height ?? artifact.Metadata.Height ?? artifact.Header.Height;
        var depth = descriptor.Depth ?? GetDepth(artifact);
        if (width is not > 0 || height is not > 0)
        {
            throw new InvalidDataException("The SANE artifact is missing image dimensions.");
        }

        var pixelFormat = GetPixelFormat(artifact, descriptor);
        var subPixelType = GetSubPixelType(pixelFormat, descriptor.Frame);
        if (pixelFormat == ImagePixelFormat.Unknown || subPixelType == null)
        {
            throw new NotSupportedException($"Unsupported SANE transfer format: {depth} bits, {descriptor.Frame} frame.");
        }

        var minimumStride = (width.Value * subPixelType.BitsPerPixel + 7) / 8;
        var stride = descriptor.Stride is > 0 ? Math.Max(descriptor.Stride.Value, minimumStride) : minimumStride;
        var frameBytes = ReadFrameBytes(payload, descriptor, stride, height.Value);
        var image = ImageContext.Create(width.Value, height.Value, pixelFormat);
        try
        {
            new CopyBitwiseImageOp().Perform(
                frameBytes,
                new PixelInfo(width.Value, height.Value, subPixelType, stride),
                image);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private IMemoryImage DecodePlanar(byte[] payload, RawScanArtifactDescriptor artifact,
        IReadOnlyList<SaneRawFrameDescriptor> frames)
    {
        if (frames.Count < 3)
        {
            throw new InvalidDataException("The planar SANE artifact does not contain three RGB frames.");
        }

        var width = frames[0].Width ?? artifact.Metadata.Width ?? artifact.Header.Width;
        var height = frames[0].Height ?? artifact.Metadata.Height ?? artifact.Header.Height;
        if (width is not > 0 || height is not > 0)
        {
            throw new InvalidDataException("The planar SANE artifact is missing image dimensions.");
        }

        var image = ImageContext.Create(width.Value, height.Value, ImagePixelFormat.RGB24);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var frame = frames[i];
                if (frame.Depth is not (null or 8))
                {
                    throw new NotSupportedException("Only 8-bit planar SANE RGB is supported.");
                }
                if (frame.Width is not { } frameWidth || frameWidth != width.Value ||
                    frame.Height is not { } frameHeightValue || frameHeightValue != height.Value)
                {
                    throw new InvalidDataException("The planar SANE frames have inconsistent dimensions.");
                }

                var stride = frame.Stride is > 0 ? frame.Stride.Value : width.Value;
                stride = Math.Max(stride, width.Value);
                var frameBytes = ReadFrameBytes(payload, frame, stride, height.Value);
                var channel = frame.Frame switch
                {
                    SaneRawFrameKind.Red => ColorChannel.Red,
                    SaneRawFrameKind.Green => ColorChannel.Green,
                    SaneRawFrameKind.Blue => ColorChannel.Blue,
                    _ => throw new InvalidDataException($"Unexpected planar SANE frame: {frame.Frame}")
                };
                new CopyBitwiseImageOp { DestChannel = channel }.Perform(
                    frameBytes,
                    new PixelInfo(width.Value, height.Value, SubPixelType.Gray, stride),
                    image);
            }
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<SaneRawFrameDescriptor> GetFrameDescriptors(RawScanArtifactDescriptor artifact)
    {
        var metadata = artifact.Metadata.AdditionalMetadata;
        if (metadata == null)
        {
            return new[] { CreateDefaultDescriptor(artifact) };
        }

        var count = GetInt(metadata, FrameCountKey) ?? 0;
        if (count <= 0)
        {
            return new[] { CreateDefaultDescriptor(artifact) };
        }

        var descriptors = new List<SaneRawFrameDescriptor>(count);
        for (var i = 0; i < count; i++)
        {
            var prefix = $"{FramePrefix}{i}.";
            if (!TryGetLong(metadata, prefix + "offset", out var offset) ||
                !TryGetLong(metadata, prefix + "length", out var length))
            {
                throw new InvalidDataException($"The SANE artifact is missing metadata for frame {i}.");
            }

            metadata.TryGetValue(prefix + "frame", out var frameValue);
            var frame = ParseFrame(frameValue);
            descriptors.Add(new SaneRawFrameDescriptor(
                frame,
                offset,
                length,
                GetInt(metadata, prefix + "width") ?? artifact.Metadata.Width ?? artifact.Header.Width,
                GetInt(metadata, prefix + "height") ?? artifact.Metadata.Height ?? artifact.Header.Height,
                GetInt(metadata, prefix + "stride") ?? artifact.Metadata.StrideOrNull(),
                GetInt(metadata, prefix + "depth")));
        }
        return descriptors;
    }

    private static SaneRawFrameDescriptor CreateDefaultDescriptor(RawScanArtifactDescriptor artifact)
    {
        var frame = (artifact.Metadata.FrameType != RawScanFrameType.Unknown
                ? artifact.Metadata.FrameType
                : artifact.Header.FrameType) switch
        {
            RawScanFrameType.Gray => SaneRawFrameKind.Gray,
            RawScanFrameType.Red => SaneRawFrameKind.Red,
            RawScanFrameType.Green => SaneRawFrameKind.Green,
            RawScanFrameType.Blue => SaneRawFrameKind.Blue,
            _ => SaneRawFrameKind.Rgb
        };
        return new SaneRawFrameDescriptor(
            frame,
            0,
            artifact.ByteLength ?? artifact.Metadata.ByteLength ?? new FileInfo(artifact.PayloadPath).Length,
            artifact.Metadata.Width ?? artifact.Header.Width,
            artifact.Metadata.Height ?? artifact.Header.Height,
            artifact.Metadata.StrideOrNull() ?? artifact.Header.Stride,
            GetDepth(artifact));
    }

    private static bool IsPlanar(RawScanArtifactDescriptor artifact,
        IReadOnlyList<SaneRawFrameDescriptor> frames)
    {
        if (artifact.Metadata.AdditionalMetadata?.TryGetValue(PlanarKey, out var planar) == true &&
            bool.TryParse(planar, out var parsed))
        {
            return parsed;
        }
        return frames[0].Frame is SaneRawFrameKind.Red or SaneRawFrameKind.Green or SaneRawFrameKind.Blue;
    }

    private static ImagePixelFormat GetPixelFormat(RawScanArtifactDescriptor artifact,
        SaneRawFrameDescriptor? descriptor)
    {
        if (artifact.Metadata.PixelFormat != ImagePixelFormat.Unknown)
        {
            return artifact.Metadata.PixelFormat;
        }
        if (artifact.Header.PixelFormat != ImagePixelFormat.Unknown)
        {
            return artifact.Header.PixelFormat;
        }
        if (descriptor == null)
        {
            return ImagePixelFormat.Unknown;
        }
        return descriptor.Frame switch
        {
            SaneRawFrameKind.Gray when descriptor.Depth == 1 => ImagePixelFormat.BW1,
            SaneRawFrameKind.Gray when descriptor.Depth == 8 => ImagePixelFormat.Gray8,
            SaneRawFrameKind.Rgb when descriptor.Depth == 8 => ImagePixelFormat.RGB24,
            SaneRawFrameKind.Red or SaneRawFrameKind.Green or SaneRawFrameKind.Blue when descriptor.Depth == 8 =>
                ImagePixelFormat.Gray8,
            _ => ImagePixelFormat.Unknown
        };
    }

    private static SubPixelType? GetSubPixelType(ImagePixelFormat format, SaneRawFrameKind frame) => format switch
    {
        ImagePixelFormat.BW1 => SubPixelType.InvertedBit,
        ImagePixelFormat.Gray8 when frame == SaneRawFrameKind.Gray => SubPixelType.Gray,
        ImagePixelFormat.Gray8 when frame is SaneRawFrameKind.Red or SaneRawFrameKind.Green or SaneRawFrameKind.Blue =>
            SubPixelType.Gray,
        ImagePixelFormat.RGB24 => SubPixelType.Rgb,
        _ => null
    };

    private static byte[] ReadFrameBytes(byte[] payload, SaneRawFrameDescriptor descriptor, int stride, int height)
    {
        if (descriptor.Offset < 0 || descriptor.Length < 0 || descriptor.Offset > payload.LongLength ||
            descriptor.Length > payload.LongLength - descriptor.Offset)
        {
            throw new InvalidDataException("The SANE frame metadata points outside the artifact payload.");
        }

        long expectedLength;
        try
        {
            expectedLength = checked((long) stride * height);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("The SANE frame layout is too large.", ex);
        }
        if (expectedLength > int.MaxValue)
        {
            throw new InvalidDataException("The SANE frame layout is too large for a managed image buffer.");
        }
        if (descriptor.Length < expectedLength)
        {
            throw new InvalidDataException("The SANE frame payload is shorter than its declared layout.");
        }

        var frameBytes = new byte[(int) expectedLength];
        Buffer.BlockCopy(payload, checked((int) descriptor.Offset), frameBytes, 0, frameBytes.Length);
        return frameBytes;
    }

    private static SaneRawFrameKind ParseFrame(string? value)
    {
        return Enum.TryParse<SaneRawFrameKind>(value, ignoreCase: true, out var frame)
            ? frame
            : SaneRawFrameKind.Rgb;
    }

    private static int? GetInt(IReadOnlyDictionary<string, string?> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) &&
               int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryGetLong(IReadOnlyDictionary<string, string?> metadata, string key, out long value)
    {
        value = 0;
        return metadata.TryGetValue(key, out var text) &&
               long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static int? GetDepth(RawScanArtifactDescriptor artifact)
    {
        if (artifact.Metadata.AdditionalMetadata?.TryGetValue("sane.frame.0.depth", out var value) == true &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth))
        {
            return depth;
        }
        return artifact.Header.PixelFormat switch
        {
            ImagePixelFormat.BW1 => 1,
            ImagePixelFormat.Gray8 or ImagePixelFormat.RGB24 => 8,
            _ => null
        };
    }

    private static void ValidateArtifact(RawScanArtifactDescriptor artifact)
    {
        if (artifact == null)
        {
            throw new ArgumentNullException(nameof(artifact));
        }
        if (string.IsNullOrWhiteSpace(artifact.PayloadPath))
        {
            throw new ArgumentException("The artifact payload path must be specified.", nameof(artifact));
        }
        if (!File.Exists(artifact.PayloadPath))
        {
            throw new FileNotFoundException("The artifact payload was not found.", artifact.PayloadPath);
        }
    }

    private enum SaneRawFrameKind
    {
        Gray,
        Rgb,
        Red,
        Green,
        Blue
    }

    private sealed record SaneRawFrameDescriptor(
        SaneRawFrameKind Frame,
        long Offset,
        long Length,
        int? Width,
        int? Height,
        int? Stride,
        int? Depth);
}

internal static class SaneRawMetadataExtensions
{
    public static int? StrideOrNull(this RawScanArtifactMetadata metadata) =>
        metadata.AdditionalMetadata?.TryGetValue("sane.frame.0.stride", out var value) == true &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stride) && stride > 0
            ? stride
            : null;
}
