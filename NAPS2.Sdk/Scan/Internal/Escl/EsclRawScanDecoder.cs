using System.Threading;
using NAPS2.Images;
using NAPS2.Images.Transforms;
using NAPS2.Pdf;
using NAPS2.Pdf.Pdfium;

namespace NAPS2.Scan;

/// <summary>
/// Decodes encoded eSCL artifacts, including PDF containers, without rendering pages that were not requested.
/// </summary>
/// <remarks>
/// eSCL scanners may return a complete multi-page PDF from one <c>NextDocument</c> response. The common raw decoder
/// can decode image and TIFF formats through <see cref="ImageContext"/>, but PDF is rendered by Pdfium here because
/// PDF is not an <see cref="ImageFileFormat"/>. This type is public so a desktop consumer can select it for eSCL
/// artifacts while keeping the driver-specific behavior in the SDK package.
/// </remarks>
public sealed class EsclRawScanDecoder : RawScanDecoder
{
    private const int DefaultPdfDpi = 300;

    public EsclRawScanDecoder(ImageContext imageContext) : base(imageContext)
    {
    }

    public override Task<IReadOnlyList<RawScanPageMetadata>> EnumeratePagesAsync(
        RawScanArtifactDescriptor artifact,
        CancellationToken cancellationToken = default)
    {
        if (!IsPdfArtifact(artifact))
        {
            return base.EnumeratePagesAsync(artifact, cancellationToken);
        }

        ValidateArtifact(artifact);
        return Task.Run(() => EnumeratePdfPages(artifact, cancellationToken), cancellationToken);
    }

    public override async Task<IMemoryImage> DecodeAsync(
        RawScanArtifactDescriptor artifact,
        int pageIndex = 0,
        int? maximumEdgeLength = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsPdfArtifact(artifact))
        {
            return await base.DecodeAsync(artifact, pageIndex, maximumEdgeLength, cancellationToken);
        }

        ValidateArtifact(artifact);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }
        if (maximumEdgeLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdgeLength));
        }

        var renderDpi = GetPdfDpi(artifact);
        var image = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Pdfium is not thread-safe. Keep the page-count check and the renderer's document open under the same
            // lock so another decoder cannot enter Pdfium between those operations.
            lock (PdfiumNativeLibrary.Instance)
            {
                using var document = PdfDocument.Load(artifact.PayloadPath);
                if (pageIndex >= document.PageCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                        $"The PDF contains fewer than {pageIndex + 1} pages.");
                }

                var previewDpi = (float)renderDpi;
                if (maximumEdgeLength is { } edge)
                {
                    using var page = document.GetPage(pageIndex);
                    previewDpi = Math.Min(previewDpi, edge * 72f / Math.Max(page.Width, page.Height));
                }

                // PdfiumPdfRenderer.RenderPage opens the same immutable file and renders only this page. It does not
                // enumerate or materialize the rest of the container.
                return new PdfiumPdfRenderer().RenderPage(
                    ImageContext,
                    artifact.PayloadPath,
                    PdfRenderSize.FromDpi(previewDpi),
                    pageIndex);
            }
        }, cancellationToken);

        if (maximumEdgeLength is not { } maxEdge || Math.Max(image.Width, image.Height) <= maxEdge)
        {
            return image;
        }

        var scale = maxEdge / (double)Math.Max(image.Width, image.Height);
        return ImageContext.PerformTransform(image, new ScaleTransform(scale));
    }

    private IReadOnlyList<RawScanPageMetadata> EnumeratePdfPages(
        RawScanArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        var pages = new List<RawScanPageMetadata>();
        var dpi = GetPdfDpi(artifact);
        lock (PdfiumNativeLibrary.Instance)
        {
            using var document = PdfDocument.Load(artifact.PayloadPath);
            for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var page = document.GetPage(pageIndex);
                pages.Add(new RawScanPageMetadata
                {
                    PageIndex = pageIndex,
                    Side = artifact.Metadata.PageSide,
                    Width = ToPixelDimension(page.Width, dpi),
                    Height = ToPixelDimension(page.Height, dpi),
                    HorizontalResolution = dpi,
                    VerticalResolution = dpi,
                    PixelFormat = ImagePixelFormat.RGB24,
                    SourceId = artifact.Metadata.SourceId ?? artifact.Header.SourceId
                });
            }
        }

        return pages;
    }

    private static int ToPixelDimension(float points, int dpi)
    {
        return Math.Max(1, (int)Math.Round(points / 72 * dpi, MidpointRounding.AwayFromZero));
    }

    private static int GetPdfDpi(RawScanArtifactDescriptor artifact)
    {
        var resolution = artifact.Metadata.HorizontalResolution ?? artifact.Header.HorizontalResolution;
        return resolution is > 0 and <= int.MaxValue
            ? (int)Math.Round(resolution.Value, MidpointRounding.AwayFromZero)
            : DefaultPdfDpi;
    }

    private static bool IsPdfArtifact(RawScanArtifactDescriptor artifact)
    {
        if (artifact == null || string.IsNullOrWhiteSpace(artifact.PayloadPath))
        {
            return false;
        }

        return string.Equals(Path.GetExtension(artifact.PayloadPath), ".pdf", StringComparison.OrdinalIgnoreCase) ||
               IsPdfContentType(artifact.Header.ContentType) ||
               IsPdfContentType(artifact.Metadata.ContentType);
    }

    private static bool IsPdfContentType(string? contentType)
    {
        return contentType?.Split(';', 2)[0].Trim().Equals("application/pdf",
            StringComparison.OrdinalIgnoreCase) == true ||
               contentType?.Split(';', 2)[0].Trim().Equals("application/x-pdf",
                   StringComparison.OrdinalIgnoreCase) == true;
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
