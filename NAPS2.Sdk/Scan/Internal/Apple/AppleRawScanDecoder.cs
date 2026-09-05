#if MACOS
using System.Globalization;
using System.Threading;
using AppKit;
using CoreGraphics;
using Foundation;
using NAPS2.Images;
using NAPS2.Images.Bitwise;
using NAPS2.Images.Mac;
using NAPS2.Images.Transforms;

namespace NAPS2.Scan;

/// <summary>
/// Decodes raw bands captured by Apple's ImageCaptureCore scanner driver.
/// </summary>
/// <remarks>
/// Acquisition stores the native band bytes and a copy of the ColorSync profile. This class performs the native
/// bitmap and ColorSync work only after the artifact has been committed, keeping ImageCaptureCore callbacks free of
/// image construction and final encoding.
/// </remarks>
public sealed class AppleRawScanDecoder : RawScanDecoder
{
    internal const string ColorSyncProfileKey = "apple.colorsync-profile-base64";
    internal const string PixelDataTypeKey = "apple.pixel-data-type";
    internal const string BitsPerComponentKey = "apple.bits-per-component";
    internal const string BitsPerPixelKey = "apple.bits-per-pixel";
    internal const string NumComponentsKey = "apple.num-components";
    internal const string BytesPerRowKey = "apple.bytes-per-row";
    internal const string PageIndexKey = "apple.page-index";

    public AppleRawScanDecoder(ImageContext imageContext) : base(imageContext)
    {
    }

    public override Task<IReadOnlyList<RawScanPageMetadata>> EnumeratePagesAsync(
        RawScanArtifactDescriptor artifact,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RawScanPageMetadata>>(
            new[]
            {
                new RawScanPageMetadata
                {
                    PageIndex = 0,
                    Side = artifact.Metadata.PageSide,
                    Width = artifact.Metadata.Width ?? artifact.Header.Width,
                    Height = artifact.Metadata.Height ?? artifact.Header.Height,
                    HorizontalResolution = artifact.Metadata.HorizontalResolution ??
                                            artifact.Header.HorizontalResolution,
                    VerticalResolution = artifact.Metadata.VerticalResolution ??
                                          artifact.Header.VerticalResolution,
                    PixelFormat = GetPixelFormat(artifact),
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
                "An Apple raw artifact contains one logical page.");
        }
        if (maximumEdgeLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdgeLength));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payload = await Task.Run(() => File.ReadAllBytes(artifact.PayloadPath), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var width = artifact.Metadata.Width ?? artifact.Header.Width;
        var height = artifact.Metadata.Height ?? artifact.Header.Height;
        if (width is not > 0 || height is not > 0)
        {
            throw new InvalidDataException("The Apple raw artifact is missing image dimensions.");
        }

        var pixelFormat = GetPixelFormat(artifact);
        var subPixelType = artifact.Metadata.SubPixelType ?? artifact.Header.SubPixelType ??
                           GetSubPixelType(pixelFormat);
        if (pixelFormat == ImagePixelFormat.Unknown || subPixelType == null)
        {
            throw new NotSupportedException("The Apple raw artifact has an unsupported pixel format.");
        }

        var metadata = artifact.Metadata.AdditionalMetadata;
        var profile = metadata?.TryGetValue(ColorSyncProfileKey, out var profileValue) == true
            ? profileValue
            : null;
        IMemoryImage? image = null;
        try
        {
            image = string.IsNullOrWhiteSpace(profile)
                ? DecodeDirect(payload, artifact, width.Value, height.Value, pixelFormat, subPixelType)
                : DecodeWithColorSync(payload, artifact, width.Value, height.Value, pixelFormat, subPixelType, profile!);

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

    private IMemoryImage DecodeDirect(byte[] payload, RawScanArtifactDescriptor artifact, int width, int height,
        ImagePixelFormat pixelFormat, SubPixelType subPixelType)
    {
        var minimumStride = (width * subPixelType.BitsPerPixel + 7) / 8;
        var stride = GetStride(artifact, minimumStride);
        var frameBytes = NormalizePayload(payload, stride, height);
        var image = ImageContext.Create(width, height, pixelFormat);
        try
        {
            new CopyBitwiseImageOp().Perform(
                frameBytes,
                new PixelInfo(width, height, subPixelType, stride),
                image);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private IMemoryImage DecodeWithColorSync(byte[] payload, RawScanArtifactDescriptor artifact, int width,
        int height, ImagePixelFormat pixelFormat, SubPixelType subPixelType, string profile)
    {
        byte[] profileBytes;
        try
        {
            profileBytes = Convert.FromBase64String(profile);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The Apple raw artifact contains an invalid ColorSync profile.", ex);
        }

        var metadata = artifact.Metadata.AdditionalMetadata;
        var bitsPerComponent = GetInt(metadata, BitsPerComponentKey) ?? (pixelFormat == ImagePixelFormat.BW1 ? 1 : 8);
        var bitsPerPixel = GetInt(metadata, BitsPerPixelKey) ?? subPixelType.BitsPerPixel;
        var stride = GetStride(artifact, (width * bitsPerPixel + 7) / 8);
        var frameBytes = NormalizePayload(payload, stride, height);
        if (pixelFormat == ImagePixelFormat.Gray8 && subPixelType == SubPixelType.Gray)
        {
            // ImageCaptureCore can report a word-aligned stride while delivering grayscale rows without the
            // alignment bytes. Preserve the existing Apple driver's ColorSync behavior for that case.
            stride = width;
        }

        using var profileData = NSData.FromArray(profileBytes);
        using var colorSpace = CGColorSpace.CreateIccData(profileData);
        using var dataProvider = new CGDataProvider(frameBytes, 0, frameBytes.Length);
        var flags = (bitsPerPixel == 32 ? CGBitmapFlags.NoneSkipLast : CGBitmapFlags.None) |
                    CGBitmapFlags.ByteOrderDefault;
        using var cgImage = new CGImage(
            width,
            height,
            bitsPerComponent,
            bitsPerPixel,
            stride,
            colorSpace,
            flags,
            dataProvider,
            null,
            true,
            CGColorRenderingIntent.Default);
        NSBitmapImageRep? imageRep = null;
        NSImage? nsImage = null;
        MacImage? macImage = null;
        IMemoryImage? copiedImage = null;
        try
        {
            imageRep = new NSBitmapImageRep(cgImage);
            nsImage = new NSImage();
            nsImage.AddRepresentation(imageRep);
            macImage = new MacImage(nsImage);

            // MacImage now owns the native image. The original representation is a temporary wrapper, matching the
            // ownership pattern used by MacImageContext.Create.
            var temporaryImageRep = imageRep!;
            imageRep = null;
            nsImage = null;
            temporaryImageRep.Dispose();

            macImage.SetResolution(
                (float) (artifact.Metadata.HorizontalResolution ?? artifact.Header.HorizontalResolution ?? 0),
                (float) (artifact.Metadata.VerticalResolution ?? artifact.Header.VerticalResolution ?? 0));
            if (ImageContext is MacImageContext)
            {
                var result = macImage;
                macImage = null;
                return result;
            }

            copiedImage = macImage.Copy(ImageContext);
            macImage.Dispose();
            macImage = null;
            var copiedResult = copiedImage!;
            copiedImage = null;
            return copiedResult;
        }
        catch
        {
            copiedImage?.Dispose();
            macImage?.Dispose();
            nsImage?.Dispose();
            imageRep?.Dispose();
            throw;
        }
    }

    private static ImagePixelFormat GetPixelFormat(RawScanArtifactDescriptor artifact) =>
        artifact.Metadata.PixelFormat != ImagePixelFormat.Unknown
            ? artifact.Metadata.PixelFormat
            : artifact.Header.PixelFormat;

    private static SubPixelType? GetSubPixelType(ImagePixelFormat pixelFormat) => pixelFormat switch
    {
        ImagePixelFormat.BW1 => SubPixelType.Bit,
        ImagePixelFormat.Gray8 => SubPixelType.Gray,
        ImagePixelFormat.RGB24 => SubPixelType.Rgb,
        ImagePixelFormat.ARGB32 => SubPixelType.Rgba,
        _ => null
    };

    private static int GetStride(RawScanArtifactDescriptor artifact, int minimumStride)
    {
        var metadataStride = GetInt(artifact.Metadata.AdditionalMetadata, BytesPerRowKey);
        return Math.Max(metadataStride ?? artifact.Header.Stride ?? minimumStride, minimumStride);
    }

    private static byte[] NormalizePayload(byte[] payload, int stride, int height)
    {
        long expectedLength;
        try
        {
            expectedLength = checked((long) stride * height);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("The Apple raw image layout is too large.", ex);
        }
        if (expectedLength > int.MaxValue)
        {
            throw new InvalidDataException("The Apple raw image layout is too large for a managed image buffer.");
        }
        if (payload.LongLength < expectedLength)
        {
            throw new InvalidDataException("The Apple raw payload is shorter than its declared layout.");
        }
        return payload;
    }

    private static int? GetInt(IReadOnlyDictionary<string, string?>? metadata, string key)
    {
        return metadata?.TryGetValue(key, out var value) == true &&
               int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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
}
#endif
