using System.Threading;
using NAPS2.Images;
using NAPS2.Images.Bitwise;
using NAPS2.Images.Transforms;

namespace NAPS2.Scan;

/// <summary>
/// Decodes committed encoded raw artifacts into caller-owned memory images.
/// </summary>
/// <remarks>
/// Driver specific raster and container formats can derive from this class and override the two decoding methods.
/// The default implementation handles the formats supported by the supplied <see cref="ImageContext"/> and treats a
/// TIFF artifact as a multi-page container.
/// </remarks>
public class RawScanDecoder
{
    private readonly ImageContext _imageContext;

    /// <summary>
    /// Gets the image context used for decoding and optional preview scaling.
    /// </summary>
    protected ImageContext ImageContext => _imageContext;

    public RawScanDecoder(ImageContext imageContext)
    {
        _imageContext = imageContext ?? throw new ArgumentNullException(nameof(imageContext));
    }

    /// <summary>
    /// Discovers the logical pages in a committed artifact without retaining decoded images.
    /// </summary>
    public virtual async Task<IReadOnlyList<RawScanPageMetadata>> EnumeratePagesAsync(
        RawScanArtifactDescriptor artifact,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);

        if (IsRasterBlockArtifact(artifact))
        {
            var layout = ParseRasterArtifact(
                artifact,
                new FileInfo(artifact.PayloadPath).Length,
                cancellationToken);
            return CreateRasterPageMetadata(artifact, layout);
        }

        var pages = new List<RawScanPageMetadata>();

        if (IsContainer(artifact))
        {
            await foreach (var image in _imageContext.LoadFrames(artifact.PayloadPath))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pages.Add(CreatePageMetadata(artifact, pages.Count, image));
                }
                finally
                {
                    image.Dispose();
                }
            }
        }
        else
        {
            using var image = LoadSingleImage(artifact);
            pages.Add(CreatePageMetadata(artifact, 0, image));
        }

        return pages;
    }

    /// <summary>
    /// Decodes one logical page from a committed artifact. The returned image is owned by the caller.
    /// </summary>
    public virtual async Task<IMemoryImage> DecodeAsync(
        RawScanArtifactDescriptor artifact,
        int pageIndex = 0,
        int? maximumEdgeLength = null,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }
        if (maximumEdgeLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdgeLength));
        }

        if (IsRasterBlockArtifact(artifact))
        {
            return await DecodeRasterAsync(artifact, pageIndex, maximumEdgeLength, cancellationToken)
                .ConfigureAwait(false);
        }

        var requestedPageIndex = pageIndex;
        IMemoryImage? decoded = null;
        if (IsContainer(artifact))
        {
            await foreach (var image in _imageContext.LoadFrames(artifact.PayloadPath))
            {
                var keepImage = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pageIndex == 0)
                    {
                        decoded = image;
                        keepImage = true;
                        break;
                    }

                    pageIndex--;
                }
                finally
                {
                    if (!keepImage)
                    {
                        image.Dispose();
                    }
                }
            }
        }
        else
        {
            if (pageIndex != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    "The artifact contains only one logical page.");
            }
            decoded = LoadSingleImage(artifact);
        }

        if (decoded == null)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedPageIndex), "The logical page was not found.");
        }

        if (maximumEdgeLength is not { } maxEdge || Math.Max(decoded.Width, decoded.Height) <= maxEdge)
        {
            return decoded;
        }

        var scale = maxEdge / (double)Math.Max(decoded.Width, decoded.Height);
        return _imageContext.PerformTransform(decoded, new ScaleTransform(scale));
    }

    private async Task<IMemoryImage> DecodeRasterAsync(
        RawScanArtifactDescriptor artifact,
        int pageIndex,
        int? maximumEdgeLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Raster decoding is deliberately deferred until after acquisition. Reading the committed payload in one
        // array also means that no image construction happens on a driver callback or on the raw writer's queue.
        var payload = await Task.Run(() => File.ReadAllBytes(artifact.PayloadPath), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var layout = ParseRasterArtifact(artifact, payload.LongLength, cancellationToken);
        if (pageIndex >= layout.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "The logical page was not found.");
        }

        var page = layout.Pages[pageIndex];
        IMemoryImage? image = null;
        try
        {
            image = ImageContext.Create(page.Width, page.Height, page.PixelFormat);
            var destinationY = 0;
            foreach (var strip in page.Strips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stripBytes = ReadStripBytes(payload, strip);
                new CopyBitwiseImageOp
                {
                    DestYOffset = destinationY,
                    Rows = strip.Height
                }.Perform(
                    stripBytes,
                    new PixelInfo(strip.Width, strip.Height, strip.SubPixelType, strip.Stride),
                    image);
                destinationY += strip.Height;
            }

            if (page.HorizontalResolution is > 0 && page.VerticalResolution is > 0)
            {
                image.SetResolution((float) page.HorizontalResolution.Value, (float) page.VerticalResolution.Value);
            }

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

    private static RasterArtifactLayout ParseRasterArtifact(
        RawScanArtifactDescriptor artifact,
        long payloadLength,
        CancellationToken cancellationToken)
    {
        var blocks = artifact.Blocks;
        if (blocks == null || blocks.Count == 0)
        {
            throw new InvalidDataException("The raster artifact does not contain block metadata.");
        }
        if (payloadLength < 0)
        {
            throw new InvalidDataException("The raster artifact payload length is invalid.");
        }

        ValidateDeclaredByteLength(artifact.ByteLength, payloadLength, "descriptor");
        ValidateDeclaredByteLength(artifact.Metadata.ByteLength, payloadLength, "metadata");

        var expectedOffset = 0L;
        for (var i = 0; i < blocks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = blocks[i];
            if (block == null || block.Length <= 0 || block.Offset != expectedOffset)
            {
                throw new InvalidDataException("The raster artifact block offsets are not contiguous.");
            }
            if (block.Layout?.Offset is { } layoutOffset && layoutOffset != block.Offset)
            {
                throw new InvalidDataException("The raster block layout offset does not match its stored offset.");
            }
            ValidateRasterBlockLayout(block.Layout);
            try
            {
                expectedOffset = checked(expectedOffset + block.Length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("The raster artifact block length overflows the payload size.", exception);
            }
        }

        if (expectedOffset != payloadLength)
        {
            throw new InvalidDataException("The raster artifact blocks do not cover the payload exactly.");
        }

        var result = new RasterArtifactLayout();
        var blockIndex = 0;
        var sawFinalBlock = false;
        while (blockIndex < blocks.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sawFinalBlock)
            {
                throw new InvalidDataException("The raster artifact contains blocks after its final block.");
            }

            var firstBlock = blocks[blockIndex];
            if (firstBlock.Layout == null)
            {
                throw new InvalidDataException("Each raster strip must start with a block layout.");
            }

            var strip = ParseRasterStrip(artifact, blocks, ref blockIndex, cancellationToken);
            var pageIndex = GetPageIndex(strip);
            if (pageIndex < 0)
            {
                throw new InvalidDataException("The raster block page index cannot be negative.");
            }

            RasterPageLayout? page = result.Pages.Count == 0 ? null : result.Pages[^1];
            if (page == null || page.PageIndex != pageIndex)
            {
                if (page != null && pageIndex <= page.PageIndex)
                {
                    throw new InvalidDataException("Raster page indexes must be ordered and cannot repeat.");
                }
                if (pageIndex != result.Pages.Count)
                {
                    throw new InvalidDataException("Raster page indexes must start at zero and be contiguous.");
                }
                page = new RasterPageLayout
                {
                    PageIndex = pageIndex,
                    HorizontalResolution = artifact.Metadata.HorizontalResolution ??
                                           artifact.Header.HorizontalResolution,
                    VerticalResolution = artifact.Metadata.VerticalResolution ??
                                         artifact.Header.VerticalResolution
                };
                result.Pages.Add(page);
            }

            AddRasterStrip(artifact, page, strip);
            sawFinalBlock |= strip.IsLastBlock;
        }

        if (!sawFinalBlock)
        {
            throw new InvalidDataException("The raster artifact does not contain an authoritative final block.");
        }

        ValidateRasterPages(artifact, result);
        return result;
    }

    private static RasterStripLayout ParseRasterStrip(
        RawScanArtifactDescriptor artifact,
        IReadOnlyList<RawScanArtifactBlock> blocks,
        ref int blockIndex,
        CancellationToken cancellationToken)
    {
        var first = blocks[blockIndex];
        var firstLayout = first.Layout!;
        var description = DescribeRasterStrip(artifact, firstLayout);
        var expectedLength = (long) description.Stride * description.Height;
        if (expectedLength <= 0)
        {
            throw new InvalidDataException("The raster block layout has no payload rows.");
        }

        var strip = new RasterStripLayout
        {
            Layout = firstLayout,
            Width = description.Width,
            Height = description.Height,
            Stride = description.Stride,
            PixelFormat = description.PixelFormat,
            SubPixelType = description.SubPixelType,
            Offset = first.Offset,
            Length = expectedLength
        };

        long copiedLength = 0;
        while (copiedLength < expectedLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blockIndex >= blocks.Count)
            {
                throw new InvalidDataException("The raster strip is shorter than its declared layout.");
            }

            var block = blocks[blockIndex];
            var blockLayout = block.Layout;
            if (blockLayout == null && copiedLength == 0)
            {
                throw new InvalidDataException("The first raster strip block must include its layout.");
            }
            if (blockLayout != null)
            {
                ValidateRasterBlockLayout(blockLayout);
                if (blockLayout.Offset is { } layoutOffset && layoutOffset != block.Offset)
                {
                    throw new InvalidDataException("The raster block layout offset does not match its stored offset.");
                }
                if (copiedLength > 0 && !AreCompatibleRasterLayouts(firstLayout, blockLayout))
                {
                    throw new InvalidDataException("Raster continuation blocks have incompatible layouts.");
                }
            }

            var remaining = expectedLength - copiedLength;
            if (block.Length > remaining)
            {
                throw new InvalidDataException("The raster block is larger than its declared strip layout.");
            }

            strip.Blocks.Add(block);
            strip.IsLastBlock |= blockLayout?.IsLastBlock == true;
            copiedLength += block.Length;
            blockIndex++;
        }

        if (copiedLength != expectedLength)
        {
            throw new InvalidDataException("The raster strip does not match its declared layout.");
        }
        return strip;
    }

    private static void AddRasterStrip(RawScanArtifactDescriptor artifact, RasterPageLayout page,
        RasterStripLayout strip)
    {
        if (page.Strips.Count == 0)
        {
            page.Width = strip.Width;
            page.PixelFormat = strip.PixelFormat;
            page.SubPixelType = strip.SubPixelType;
            page.Offset = strip.Offset;
        }
        else if (page.Width != strip.Width || page.PixelFormat != strip.PixelFormat ||
                 !AreEquivalentSubPixelTypes(page.SubPixelType, strip.SubPixelType))
        {
            throw new InvalidDataException("Raster strips in one page have incompatible layouts.");
        }

        try
        {
            page.Height = checked(page.Height + strip.Height);
            page.Length = checked(page.Length + strip.Length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The raster page layout is too large.", exception);
        }
        page.Strips.Add(strip);
    }

    private static void ValidateRasterPages(RawScanArtifactDescriptor artifact, RasterArtifactLayout layout)
    {
        if (layout.Pages.Count == 0)
        {
            throw new InvalidDataException("The raster artifact does not contain any logical pages.");
        }

        ValidateDeclaredPageCount(artifact.Metadata.PageCount, layout.Pages.Count, "metadata");
        ValidateDeclaredPageCount(artifact.Header.PageCount, layout.Pages.Count, "header");

        if (layout.Pages.Count == 1)
        {
            ValidateDeclaredDimension(artifact.Metadata.Width, layout.Pages[0].Width, "metadata width");
            ValidateDeclaredDimension(artifact.Metadata.Height, layout.Pages[0].Height, "metadata height");
            ValidateDeclaredDimension(artifact.Header.Width, layout.Pages[0].Width, "header width");
            ValidateDeclaredDimension(artifact.Header.Height, layout.Pages[0].Height, "header height");
        }

        var declaredPages = artifact.Pages;
        if (declaredPages != null && declaredPages.Count != 0)
        {
            if (declaredPages.Count != layout.Pages.Count)
            {
                throw new InvalidDataException("The declared raster pages do not match the block pages.");
            }
            foreach (var page in layout.Pages)
            {
                var declared = declaredPages.FirstOrDefault(x => x.PageIndex == page.PageIndex);
                if (declared == null)
                {
                    throw new InvalidDataException("The declared raster pages are missing a page index.");
                }
                ValidateDeclaredDimension(declared.Width, page.Width, "page width");
                ValidateDeclaredDimension(declared.Height, page.Height, "page height");
                if (declared.PixelFormat != ImagePixelFormat.Unknown && declared.PixelFormat != page.PixelFormat)
                {
                    throw new InvalidDataException("The declared raster page pixel format is incorrect.");
                }
                if (declared.Offset is { } offset && offset != page.Offset ||
                    declared.Length is { } length && length != page.Length)
                {
                    throw new InvalidDataException("The declared raster page range is incorrect.");
                }
                page.HorizontalResolution = declared.HorizontalResolution ?? page.HorizontalResolution;
                page.VerticalResolution = declared.VerticalResolution ?? page.VerticalResolution;
            }
        }
    }

    private static IReadOnlyList<RawScanPageMetadata> CreateRasterPageMetadata(
        RawScanArtifactDescriptor artifact,
        RasterArtifactLayout layout)
    {
        var pages = new List<RawScanPageMetadata>(layout.Pages.Count);
        foreach (var page in layout.Pages)
        {
            var declared = artifact.Pages?.FirstOrDefault(x => x.PageIndex == page.PageIndex);
            pages.Add(new RawScanPageMetadata
            {
                PageIndex = page.PageIndex,
                Side = declared?.Side is not (null or RawScanPageSide.Unknown)
                    ? declared.Side
                    : artifact.Metadata.PageSide,
                Width = page.Width,
                Height = page.Height,
                HorizontalResolution = declared?.HorizontalResolution ??
                                        artifact.Metadata.HorizontalResolution ?? artifact.Header.HorizontalResolution,
                VerticalResolution = declared?.VerticalResolution ??
                                      artifact.Metadata.VerticalResolution ?? artifact.Header.VerticalResolution,
                PixelFormat = page.PixelFormat,
                Offset = page.Offset,
                Length = page.Length,
                SourceId = declared?.SourceId ?? artifact.Metadata.SourceId ?? artifact.Header.SourceId
            });
        }
        return pages;
    }

    private static byte[] ReadStripBytes(byte[] payload, RasterStripLayout strip)
    {
        if (strip.Length > int.MaxValue)
        {
            throw new InvalidDataException("The raster strip is too large for a managed image buffer.");
        }

        var bytes = new byte[(int) strip.Length];
        var destinationOffset = 0;
        foreach (var block in strip.Blocks)
        {
            if (block.Offset < 0 || block.Offset > payload.LongLength - block.Length ||
                block.Length > bytes.Length - destinationOffset)
            {
                throw new InvalidDataException("The raster block points outside the artifact payload.");
            }
            Buffer.BlockCopy(payload, checked((int) block.Offset), bytes, destinationOffset, block.Length);
            destinationOffset += block.Length;
        }
        if (destinationOffset != bytes.Length)
        {
            throw new InvalidDataException("The raster strip does not cover its declared byte range.");
        }
        return bytes;
    }

    private static RasterStripDescription DescribeRasterStrip(
        RawScanArtifactDescriptor artifact,
        RawBlockLayout layout)
    {
        var width = layout.Width ?? artifact.Metadata.Width ?? artifact.Header.Width;
        var height = layout.Height ?? artifact.Metadata.Height ?? artifact.Header.Height;
        if (width is not > 0 || height is not > 0)
        {
            throw new InvalidDataException("The raster block is missing image dimensions.");
        }

        var subPixelType = layout.SubPixelType ?? artifact.Metadata.SubPixelType ?? artifact.Header.SubPixelType;
        var bitsPerPixel = layout.BitsPerPixel ?? subPixelType?.BitsPerPixel;
        var pixelFormat = layout.PixelFormat != ImagePixelFormat.Unknown
            ? layout.PixelFormat
            : artifact.Metadata.PixelFormat != ImagePixelFormat.Unknown
                ? artifact.Metadata.PixelFormat
                : artifact.Header.PixelFormat;
        if (bitsPerPixel is null)
        {
            bitsPerPixel = GetBitsPerPixel(pixelFormat);
        }
        if (pixelFormat == ImagePixelFormat.Unknown)
        {
            pixelFormat = InferPixelFormat(bitsPerPixel, subPixelType);
        }
        if (subPixelType == null)
        {
            subPixelType = GetSubPixelType(pixelFormat, bitsPerPixel);
        }
        if (pixelFormat == ImagePixelFormat.Unknown || subPixelType == null || bitsPerPixel is not > 0)
        {
            throw new NotSupportedException("The raster artifact has an unsupported pixel representation.");
        }
        if (bitsPerPixel.Value != subPixelType.BitsPerPixel)
        {
            throw new InvalidDataException("The raster block bit depth does not match its pixel representation.");
        }
        ValidatePixelRepresentation(pixelFormat, subPixelType, bitsPerPixel.Value);

        if (layout.BytesPerPixel is { } bytesPerPixel)
        {
            if (bytesPerPixel < 0 || (bitsPerPixel.Value >= 8 && bytesPerPixel != subPixelType.BytesPerPixel) ||
                (bitsPerPixel.Value < 8 && bytesPerPixel is not (0 or 1)))
            {
                throw new InvalidDataException("The raster block bytes per pixel value is invalid.");
            }
        }

        long minimumStride;
        try
        {
            minimumStride = checked(((long) width.Value * bitsPerPixel.Value + 7) / 8);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The raster block stride is too large.", exception);
        }
        if (minimumStride > int.MaxValue)
        {
            throw new InvalidDataException("The raster block stride is too large for a managed image buffer.");
        }

        var stride = layout.Stride ?? artifact.Header.Stride ?? (int) minimumStride;
        if (stride < minimumStride || stride <= 0)
        {
            throw new InvalidDataException("The raster block stride is smaller than its row data.");
        }

        return new RasterStripDescription(width.Value, height.Value, stride, pixelFormat, subPixelType);
    }

    private static void ValidateRasterBlockLayout(RawBlockLayout? layout)
    {
        if (layout == null)
        {
            return;
        }
        if (layout.Offset is < 0 || layout.Width is <= 0 || layout.Height is <= 0 || layout.Stride is <= 0 ||
            layout.BitsPerPixel is <= 0 || layout.BytesPerPixel is < 0 || layout.ChannelIndex is < 0 ||
            layout.ChannelCount is <= 0 || layout.PageIndex is < 0 || layout.FrameIndex is < 0)
        {
            throw new InvalidDataException("The raster block layout contains invalid dimensions or indexes.");
        }
    }

    private static bool AreCompatibleRasterLayouts(RawBlockLayout expected, RawBlockLayout actual)
    {
        return Compatible(expected.Width, actual.Width) && Compatible(expected.Height, actual.Height) &&
               Compatible(expected.Stride, actual.Stride) && Compatible(expected.BitsPerPixel, actual.BitsPerPixel) &&
               Compatible(expected.BytesPerPixel, actual.BytesPerPixel) &&
               Compatible(expected.PixelFormat, actual.PixelFormat) &&
               CompatibleSubPixelTypes(expected.SubPixelType, actual.SubPixelType) &&
               Compatible(expected.FrameType, actual.FrameType) && Compatible(expected.ChannelIndex, actual.ChannelIndex) &&
               Compatible(expected.ChannelCount, actual.ChannelCount) && Compatible(expected.PageIndex, actual.PageIndex) &&
               Compatible(expected.FrameIndex, actual.FrameIndex);
    }

    private static bool Compatible<T>(T expected, T actual)
    {
        return EqualityComparer<T>.Default.Equals(expected, default!) ||
               EqualityComparer<T>.Default.Equals(actual, default!) ||
               EqualityComparer<T>.Default.Equals(expected, actual);
    }

    private static bool CompatibleSubPixelTypes(SubPixelType? expected, SubPixelType? actual)
    {
        return expected == null || actual == null || AreEquivalentSubPixelTypes(expected, actual);
    }

    private static bool AreEquivalentSubPixelTypes(SubPixelType first, SubPixelType second)
    {
        return first.BitsPerPixel == second.BitsPerPixel && first.BytesPerPixel == second.BytesPerPixel &&
               first.RedOffset == second.RedOffset && first.GreenOffset == second.GreenOffset &&
               first.BlueOffset == second.BlueOffset && first.AlphaOffset == second.AlphaOffset &&
               first.HasAlpha == second.HasAlpha && first.InvertColorSpace == second.InvertColorSpace;
    }

    private static int GetPageIndex(RasterStripLayout strip)
    {
        foreach (var block in strip.Blocks)
        {
            if (block.Layout?.PageIndex is { } pageIndex)
            {
                return pageIndex;
            }
        }
        return 0;
    }

    private static int? GetBitsPerPixel(ImagePixelFormat pixelFormat) => pixelFormat switch
    {
        ImagePixelFormat.BW1 => 1,
        ImagePixelFormat.Gray8 => 8,
        ImagePixelFormat.RGB24 => 24,
        ImagePixelFormat.ARGB32 => 32,
        _ => null
    };

    private static ImagePixelFormat InferPixelFormat(int? bitsPerPixel, SubPixelType? subPixelType) =>
        bitsPerPixel switch
        {
            1 => ImagePixelFormat.BW1,
            8 when subPixelType == null || subPixelType.BytesPerPixel == 1 => ImagePixelFormat.Gray8,
            24 => ImagePixelFormat.RGB24,
            32 when subPixelType?.HasAlpha == true => ImagePixelFormat.ARGB32,
            32 => ImagePixelFormat.RGB24,
            _ => ImagePixelFormat.Unknown
        };

    private static SubPixelType? GetSubPixelType(ImagePixelFormat pixelFormat, int? bitsPerPixel) => pixelFormat switch
    {
        ImagePixelFormat.BW1 => SubPixelType.Bit,
        ImagePixelFormat.Gray8 => SubPixelType.Gray,
        ImagePixelFormat.RGB24 when bitsPerPixel == 32 => SubPixelType.Rgbn,
        ImagePixelFormat.RGB24 => SubPixelType.Rgb,
        ImagePixelFormat.ARGB32 => SubPixelType.Rgba,
        _ => null
    };

    private static void ValidatePixelRepresentation(ImagePixelFormat pixelFormat, SubPixelType subPixelType,
        int bitsPerPixel)
    {
        var valid = pixelFormat switch
        {
            ImagePixelFormat.BW1 => bitsPerPixel == 1,
            ImagePixelFormat.Gray8 => bitsPerPixel == 8 && subPixelType.BytesPerPixel == 1,
            ImagePixelFormat.RGB24 => bitsPerPixel is 24 or 32 && !subPixelType.HasAlpha,
            ImagePixelFormat.ARGB32 => bitsPerPixel == 32 && subPixelType.HasAlpha,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException("The raster block pixel format and bit depth are inconsistent.");
        }
    }

    private static void ValidateDeclaredByteLength(long? declared, long actual, string source)
    {
        if (declared is < 0 || declared is { } value && value != actual)
        {
            throw new InvalidDataException($"The raster artifact {source} byte length does not match its payload.");
        }
    }

    private static void ValidateDeclaredPageCount(int? declared, int actual, string source)
    {
        if (declared is <= 0 || declared is { } value && value != actual)
        {
            throw new InvalidDataException($"The raster artifact {source} page count does not match its blocks.");
        }
    }

    private static void ValidateDeclaredDimension(int? declared, int actual, string source)
    {
        if (declared is <= 0 || declared is { } value && value != actual)
        {
            throw new InvalidDataException($"The raster artifact {source} does not match its blocks.");
        }
    }

    private sealed class RasterArtifactLayout
    {
        public List<RasterPageLayout> Pages { get; } = [];
    }

    private sealed class RasterPageLayout
    {
        public int PageIndex { get; init; }
        public int Width { get; set; }
        public int Height { get; set; }
        public ImagePixelFormat PixelFormat { get; set; }
        public SubPixelType SubPixelType { get; set; } = null!;
        public double? HorizontalResolution { get; set; }
        public double? VerticalResolution { get; set; }
        public long Offset { get; set; }
        public long Length { get; set; }
        public List<RasterStripLayout> Strips { get; } = [];
    }

    private sealed class RasterStripLayout
    {
        public RawBlockLayout Layout { get; init; } = null!;
        public int Width { get; init; }
        public int Height { get; init; }
        public int Stride { get; init; }
        public ImagePixelFormat PixelFormat { get; init; }
        public SubPixelType SubPixelType { get; init; } = null!;
        public long Offset { get; init; }
        public long Length { get; init; }
        public bool IsLastBlock { get; set; }
        public List<RawScanArtifactBlock> Blocks { get; } = [];
    }

    private sealed record RasterStripDescription(
        int Width,
        int Height,
        int Stride,
        ImagePixelFormat PixelFormat,
        SubPixelType SubPixelType);

    private IMemoryImage LoadSingleImage(RawScanArtifactDescriptor artifact)
    {
        return _imageContext.Load(artifact.PayloadPath);
    }

    private static RawScanPageMetadata CreatePageMetadata(
        RawScanArtifactDescriptor artifact,
        int pageIndex,
        IMemoryImage image)
    {
        var metadata = artifact.Metadata;
        return new RawScanPageMetadata
        {
            PageIndex = pageIndex,
            Side = metadata.PageSide,
            Width = image.Width,
            Height = image.Height,
            HorizontalResolution = image.HorizontalResolution,
            VerticalResolution = image.VerticalResolution,
            PixelFormat = image.PixelFormat,
            SourceId = metadata.SourceId ?? artifact.Header.SourceId
        };
    }

    private static bool IsContainer(RawScanArtifactDescriptor artifact)
    {
        return artifact.Header.Type == RawScanArtifactType.EncodedContainer ||
               artifact.Metadata.PageCount is > 1 ||
               artifact.Header.PageCount is > 1 ||
               artifact.Header.ImageFormat == ImageFileFormat.Tiff ||
               artifact.Metadata.ImageFormat == ImageFileFormat.Tiff;
    }

    private static bool IsRasterBlockArtifact(RawScanArtifactDescriptor artifact)
    {
        return artifact.Header.Type == RawScanArtifactType.RasterBlock;
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
