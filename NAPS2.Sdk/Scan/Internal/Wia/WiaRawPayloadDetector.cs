using NAPS2.Images;

namespace NAPS2.Scan.Internal.Wia;

/// <summary>
/// Describes the representation of a WIA transfer without decoding it.
/// </summary>
internal sealed record WiaRawPayloadInfo(RawScanArtifactType Type, ImageFileFormat ImageFormat,
    string? ContentType, string? FileExtension, bool IsContainer);

/// <summary>
/// Identifies common WIA transfer formats from their bytes and optional file name.
/// </summary>
internal static class WiaRawPayloadDetector
{
    public static WiaRawPayloadInfo Detect(Stream stream, string? pathHint = null)
    {
        var format = pathHint == null ? ImageFileFormat.Unknown : ImageContext.GetFileFormatFromExtension(pathHint);
        var origin = stream.CanSeek ? stream.Position : 0;
        var firstBytes = new byte[8];
        var bytesRead = 0;
        if (stream.CanSeek)
        {
            stream.Position = 0;
            bytesRead = stream.Read(firstBytes, 0, firstBytes.Length);
        }
        if (stream.CanSeek)
        {
            stream.Position = origin;
        }

        if (bytesRead >= 4 && firstBytes[0] == 0x25 && firstBytes[1] == 0x50 &&
            firstBytes[2] == 0x44 && firstBytes[3] == 0x46)
        {
            return new WiaRawPayloadInfo(RawScanArtifactType.EncodedContainer, ImageFileFormat.Unknown,
                "application/pdf", ".pdf", true);
        }

        if (format == ImageFileFormat.Unknown && bytesRead > 0)
        {
            format = ImageContext.GetFileFormatFromFirstBytes(firstBytes);
        }

        // ImageContext intentionally only recognizes raster extensions. WIA 2.0 native UI can still return a PDF
        // file, so preserve that container metadata when the driver provides only the file name.
        if (format == ImageFileFormat.Unknown &&
            string.Equals(Path.GetExtension(pathHint), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return new WiaRawPayloadInfo(RawScanArtifactType.EncodedContainer, ImageFileFormat.Unknown,
                "application/pdf", ".pdf", true);
        }

        var isContainer = format == ImageFileFormat.Tiff;
        return new WiaRawPayloadInfo(
            isContainer
                ? RawScanArtifactType.EncodedContainer
                : format == ImageFileFormat.Unknown ? RawScanArtifactType.Unknown : RawScanArtifactType.EncodedImage,
            format,
            GetContentType(format),
            GetExtension(format) ?? GetExtensionFromPath(pathHint),
            isContainer);
    }

    private static string? GetContentType(ImageFileFormat format) => format switch
    {
        ImageFileFormat.Png => "image/png",
        ImageFileFormat.Jpeg => "image/jpeg",
        ImageFileFormat.Bmp => "image/bmp",
        ImageFileFormat.Tiff => "image/tiff",
        ImageFileFormat.Jpeg2000 => "image/jp2",
        _ => null
    };

    private static string? GetExtension(ImageFileFormat format) => format switch
    {
        ImageFileFormat.Png => ".png",
        ImageFileFormat.Jpeg => ".jpg",
        ImageFileFormat.Bmp => ".bmp",
        ImageFileFormat.Tiff => ".tiff",
        ImageFileFormat.Jpeg2000 => ".jp2",
        _ => null
    };

    private static string? GetExtensionFromPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetExtension(path);
}
