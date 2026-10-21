#if !MACOS
using System.Buffers;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using NAPS2.Images;
using NAPS2.Remoting.Worker;
using NTwain;
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// Adapts NTwain's synchronous transfer callbacks to the raw scan sink.
/// </summary>
/// <remarks>
/// NTwain owns the native transfer memory only for the duration of the callback. This adapter calls the sink while
/// that memory is valid and never creates an <see cref="IMemoryImage"/> or a <see cref="ProcessedImage"/>. Native and
/// file transfers are complete image payloads. Memory transfers remain open until NTwain marks the final strip with
/// <see cref="DataTransferredEventArgs.IsTransferComplete"/>.
/// </remarks>
internal sealed class TwainRawScanSink : IDisposable
{
    private const int StreamBufferSize = 128 * 1024;
    private const string TransferCompletePropertyName = "IsTransferComplete";

    private readonly RawScanOptions _options;
    private readonly IRawScanSink _sink;
    private readonly IScanEvents _scanEvents;
    private readonly ILogger _logger;
    private IRawScanArtifactWriter? _writer;
    private TwainImageData? _pendingImageData;
    private TwainMemoryPayloadKind _payloadKind;
    private FileStream? _group4Spool;
    private long _currentByteLength;
    private int _pageIndex = -1;
    private bool _disposed;

    public TwainRawScanSink(RawScanOptions options, IRawScanSink sink, IScanEvents scanEvents, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _scanEvents = scanEvents ?? throw new ArgumentNullException(nameof(scanEvents));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets whether the loaded NTwain assembly exposes the completion bit needed to commit memory strips safely.
    /// Reflection keeps a desktop built against EVOSCAN NTwain able to fail clearly when an older assembly is loaded
    /// at runtime; the native transfer fallback does not need to read this property.
    /// </summary>
    internal static bool HasTransferCompletionFlag =>
        typeof(DataTransferredEventArgs).GetProperty(TransferCompletePropertyName,
            BindingFlags.Instance | BindingFlags.Public) != null;

    public void ConfigurationApplied(DriverProcessingResult result)
    {
        ThrowIfDisposed();
        _sink.ConfigurationApplied(result ?? throw new ArgumentNullException(nameof(result)));
    }

    public void PageStart(TransferReadyEventArgs transferReady)
    {
        ThrowIfDisposed();
        if (_writer != null)
        {
            throw new TwainRawTransferCompletionUnavailableException(
                "TWAIN delivered another transfer before exposing native completion for the previous memory transfer.");
        }

        BeginPage(transferReady.PendingImageInfo);
    }

    public void DataTransferred(DataTransferredEventArgs transfer)
    {
        ThrowIfDisposed();
        if (_writer == null)
        {
            // TransferReady is expected for every image, but some sources deliver a native transfer directly. Keep
            // the raw stream usable in that case while still emitting the same page event and page index.
            BeginPage(transfer.ImageInfo);
        }

        switch (transfer.TransferType)
        {
            case XferMech.Memory:
            {
                var isTransferComplete = GetTransferCompletion(transfer);
                WriteMemoryBuffer(transfer, isTransferComplete);
                if (isTransferComplete)
                {
                    CompleteMemoryTransfer(transfer);
                }
                return;
            }
            case XferMech.Native:
                // NTwain raises one native callback only after ImageNativeXfer returns XFERDONE. Unlike memory
                // strips, there is no intermediate native callback to keep open; the callback itself is the complete
                // transfer boundary (including when running against an older NTwain assembly).
                using (var stream = transfer.GetNativeImageStream())
                {
                    if (stream == null)
                    {
                        throw new InvalidOperationException("TWAIN returned a native transfer without an image stream.");
                    }
                    CopyStream(stream);
                }
                CompleteCurrent(transfer.ImageInfo, transfer, RawScanArtifactType.NativeBitmap, ImageFileFormat.Bmp);
                return;
            case XferMech.File:
                // File and memory-file routines publish this callback only after the file transfer returns XFERDONE;
                // copy the committed file while it is still owned by the source session.
                if (string.IsNullOrWhiteSpace(transfer.FileDataPath))
                {
                    throw new InvalidOperationException("TWAIN returned a file transfer without a file path.");
                }
                using (var stream = File.OpenRead(transfer.FileDataPath))
                {
                    CopyStream(stream);
                }
                CompleteCurrent(transfer.ImageInfo, transfer, RawScanArtifactType.EncodedImage,
                    MapFileFormat(transfer.ImageFileFormat));
                return;
            case XferMech.MemFile:
                throw new NotSupportedException("TWAIN memory-file transfer cannot be committed as a raw artifact.");
            default:
                throw new NotSupportedException($"Unsupported TWAIN transfer mechanism: {transfer.TransferType}.");
        }
    }

    public void TransferCanceled()
    {
        if (_disposed) return;
        AbortCurrent();
    }

    /// <summary>
    /// Ensures that no incomplete artifact is presented as a successful raw scan.
    /// </summary>
    public void CompleteScan()
    {
        ThrowIfDisposed();
        if (_writer == null) return;

        AbortCurrent();
        throw new TwainRawTransferCompletionUnavailableException(
            "TWAIN ended a memory transfer without an IsTransferComplete=true event; the raw artifact was discarded.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AbortCurrent();
    }

    private void BeginPage(TWImageInfo? imageInfo)
    {
        _pageIndex++;
        _currentByteLength = 0;
        _pendingImageData = ToImageData(imageInfo);
        // Classify before the artifact is begun: an unsupported compression then fails the scan with its own error
        // instead of leaving an open artifact whose abort would be reported first.
        _payloadKind = _options.TwainOptions.TransferMode == TwainTransferMode.Memory && imageInfo != null
            ? TwainMemoryTransferPayload.Classify(imageInfo.Compression)
            : TwainMemoryPayloadKind.Raster;
        ReleaseGroup4Spool();
        if (_payloadKind == TwainMemoryPayloadKind.Group4Tiff)
        {
            // The TIFF header needs the stream length, so the stream is spooled to a temporary file and copied to
            // the writer in bounded chunks at completion. Memory stays bounded however large the page is, which
            // matters in the 32-bit worker.
            _group4Spool = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, StreamBufferSize,
                FileOptions.DeleteOnClose);
        }
        _scanEvents.PageStart();
        _writer = _sink.BeginArtifact(CreateHeader(_pendingImageData));
    }

    private void WriteMemoryBuffer(DataTransferredEventArgs transfer, bool isTransferComplete)
    {
        if (transfer.MemoryData == null || transfer.MemoryInfo == null)
        {
            throw new InvalidOperationException("TWAIN returned an incomplete memory transfer event.");
        }

        var memoryInfo = transfer.MemoryInfo;
        var bufferKind = TwainMemoryTransferPayload.Classify(memoryInfo.Compression);
        if (bufferKind != _payloadKind)
        {
            throw new InvalidOperationException(
                $"TWAIN announced a {_payloadKind} memory transfer but delivered {memoryInfo.Compression} data.");
        }
        switch (_payloadKind)
        {
            case TwainMemoryPayloadKind.Jpeg:
            {
                ReadOnlySpan<byte> data = transfer.MemoryData;
                // Decoders take the resolution from the JPEG itself, so put the TWAIN resolution there.
                if (_currentByteLength == 0 && TwainMemoryTransferPayload.TryApplyJfifDensity(data,
                        _pendingImageData?.XRes ?? 0, _pendingImageData?.YRes ?? 0, out var patched))
                {
                    data = patched;
                }
                _writer!.Write(data);
                _currentByteLength = checked(_currentByteLength + data.Length);
                return;
            }
            case TwainMemoryPayloadKind.Group4Tiff:
                _group4Spool!.Write(transfer.MemoryData, 0, transfer.MemoryData.Length);
                return;
        }

        var imageData = _pendingImageData ?? ToImageData(transfer.ImageInfo);
        var (width, height, stride) = TwainMemoryTransferPayload.ResolveRasterStrip(
            memoryInfo.Columns, memoryInfo.Rows, memoryInfo.BytesPerRow, transfer.MemoryData.Length,
            imageData?.Width ?? 0, imageData?.BitsPerPixel ?? 0);

        var (pixelFormat, subPixelType) = MapPixelFormat(imageData);
        _writer!.Write(transfer.MemoryData, new RawBlockLayout
        {
            Offset = _currentByteLength,
            Width = width,
            Height = height,
            Stride = stride,
            BitsPerPixel = imageData?.BitsPerPixel,
            BytesPerPixel = imageData == null || imageData.BitsPerPixel < 8
                ? null
                : imageData.BitsPerPixel / 8,
            PixelFormat = pixelFormat,
            SubPixelType = subPixelType,
            FrameType = RawScanFrameType.Image,
            PageIndex = _pageIndex,
            FrameIndex = 0,
            IsLastBlock = isTransferComplete
        });
        _currentByteLength = checked(_currentByteLength + transfer.MemoryData.Length);
    }

    private void CopyStream(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                _writer!.Write(buffer.AsSpan(0, read));
                _currentByteLength = checked(_currentByteLength + read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void CompleteMemoryTransfer(DataTransferredEventArgs transfer)
    {
        switch (_payloadKind)
        {
            case TwainMemoryPayloadKind.Jpeg:
                CompleteCurrent(transfer.ImageInfo, transfer, RawScanArtifactType.EncodedImage, ImageFileFormat.Jpeg);
                return;
            case TwainMemoryPayloadKind.Group4Tiff:
            {
                // The final image info is authoritative; sources that scan to an unknown length report -1 up front.
                var imageData = ToImageData(transfer.ImageInfo) ?? _pendingImageData;
                using (var spool = _group4Spool!)
                {
                    _group4Spool = null;
                    var header = TwainMemoryTransferPayload.CreateGroup4TiffHeader(spool.Length,
                        imageData?.Width ?? 0, imageData?.Height ?? 0, imageData?.XRes ?? 0, imageData?.YRes ?? 0);
                    _writer!.Write(header);
                    _currentByteLength = header.Length;
                    spool.Position = 0;
                    CopyStream(spool);
                }
                CompleteCurrent(transfer.ImageInfo, transfer, RawScanArtifactType.EncodedImage, ImageFileFormat.Tiff);
                return;
            }
            default:
                CompleteCurrent(transfer.ImageInfo, transfer, RawScanArtifactType.RasterBlock, ImageFileFormat.Unknown);
                return;
        }
    }

    private void CompleteCurrent(TWImageInfo? imageInfo, DataTransferredEventArgs? transfer,
        RawScanArtifactType type, ImageFileFormat format)
    {
        if (_writer == null) return;
        var writer = _writer;
        _writer = null;
        try
        {
            writer.CompleteAsync(CreateMetadata(imageInfo, transfer, type, format), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch
        {
            try
            {
                writer.AbortAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception abortException)
            {
                _logger.LogDebug(abortException, "Error aborting a TWAIN raw artifact after completion failure");
            }
            throw;
        }
    }

    private static bool GetTransferCompletion(DataTransferredEventArgs transfer)
    {
        try
        {
            return transfer.IsTransferComplete;
        }
        catch (MissingMemberException ex)
        {
            throw new TwainRawTransferCompletionUnavailableException(
                "The loaded NAPS2.NTwain assembly does not expose DataTransferredEventArgs.IsTransferComplete for raw memory transfers.",
                ex);
        }
    }

    private void ReleaseGroup4Spool()
    {
        // Disposing deletes the temporary file (FileOptions.DeleteOnClose).
        _group4Spool?.Dispose();
        _group4Spool = null;
    }

    private void AbortCurrent()
    {
        var writer = _writer;
        _writer = null;
        ReleaseGroup4Spool();
        _currentByteLength = 0;
        if (writer == null) return;
        try
        {
            writer.AbortAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error aborting a TWAIN raw artifact");
        }
    }

    private RawScanArtifactHeader CreateHeader(TwainImageData? imageData)
    {
        var (pixelFormat, subPixelType) = MapPixelFormat(imageData);
        // The header must name the payload format from the start: consumers pick the payload file name and decoder
        // from it before the transfer completes.
        var (type, format, contentType, extension) = _options.TwainOptions.TransferMode == TwainTransferMode.Native
            ? (RawScanArtifactType.NativeBitmap, ImageFileFormat.Bmp, "image/bmp", ".bmp")
            : _payloadKind switch
            {
                TwainMemoryPayloadKind.Jpeg =>
                    (RawScanArtifactType.EncodedImage, ImageFileFormat.Jpeg, "image/jpeg", ".jpg"),
                TwainMemoryPayloadKind.Group4Tiff =>
                    (RawScanArtifactType.EncodedImage, ImageFileFormat.Tiff, "image/tiff", ".tif"),
                _ => (RawScanArtifactType.RasterBlock, ImageFileFormat.Unknown, (string?) null, (string?) null)
            };
        return new RawScanArtifactHeader
        {
            Type = type,
            ImageFormat = format,
            ContentType = contentType,
            FileExtension = extension,
            PixelFormat = pixelFormat,
            SubPixelType = subPixelType,
            FrameType = RawScanFrameType.Image,
            Width = imageData?.Width > 0 ? imageData.Width : null,
            Height = imageData?.Height > 0 ? imageData.Height : null,
            HorizontalResolution = imageData?.XRes > 0 ? imageData.XRes : null,
            VerticalResolution = imageData?.YRes > 0 ? imageData.YRes : null,
            PageCount = 1,
            SourceId = _options.Device?.ID
        };
    }

    private RawScanArtifactMetadata CreateMetadata(TWImageInfo? imageInfo, DataTransferredEventArgs? transfer,
        RawScanArtifactType type, ImageFileFormat format)
    {
        var imageData = ToImageData(imageInfo) ?? _pendingImageData;
        var (pixelFormat, subPixelType) = MapPixelFormat(imageData);
        // Read results for every completed transfer. This also captures barcode detection configured inside the
        // source's native TWAIN UI, where ConfigureSource deliberately leaves the driver's settings untouched.
        var extendedImageMetadata = TwainExtendedImageMetadataReader.Read(transfer);
        var additionalMetadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["twain.transferType"] = type.ToString()
        };
        foreach (var (key, value) in extendedImageMetadata.AdditionalMetadata)
        {
            additionalMetadata[key] = value;
        }
        var metadata = new RawScanArtifactMetadata
        {
            ByteLength = _currentByteLength,
            Width = imageData?.Width > 0 ? imageData.Width : null,
            Height = imageData?.Height > 0 ? imageData.Height : null,
            HorizontalResolution = imageData?.XRes > 0 ? imageData.XRes : null,
            VerticalResolution = imageData?.YRes > 0 ? imageData.YRes : null,
            PixelFormat = pixelFormat,
            SubPixelType = subPixelType,
            FrameType = RawScanFrameType.Image,
            ImageFormat = format,
            ContentType = format switch
            {
                ImageFileFormat.Bmp => "image/bmp",
                ImageFileFormat.Jpeg => "image/jpeg",
                ImageFileFormat.Tiff => "image/tiff",
                _ => null
            },
            PageCount = 1,
            FrameCount = 1,
            PageSide = extendedImageMetadata.PageSide,
            IsDuplex = _options.PaperSource == PaperSource.Duplex,
            DeviceId = _options.Device?.ID,
            SourceId = _options.Device?.ID,
            AdditionalMetadata = additionalMetadata
        };
        return metadata;
    }

    private static TwainImageData? ToImageData(TWImageInfo? imageInfo)
    {
        if (imageInfo == null) return null;
        var imageData = new TwainImageData
        {
            Width = imageInfo.ImageWidth,
            Height = imageInfo.ImageLength,
            BitsPerPixel = imageInfo.BitsPerPixel,
            SamplesPerPixel = imageInfo.SamplesPerPixel,
            PixelType = (int) imageInfo.PixelType,
            XRes = imageInfo.XResolution,
            YRes = imageInfo.YResolution
        };
        imageData.BitsPerSample.AddRange(imageInfo.BitsPerSample.Select(x => (int) x));
        return imageData;
    }

    private static (ImagePixelFormat PixelFormat, SubPixelType? SubPixelType) MapPixelFormat(
        TwainImageData? imageData)
    {
        if (imageData == null) return (ImagePixelFormat.Unknown, null);
        return (imageData.PixelType, imageData.BitsPerPixel, imageData.SamplesPerPixel) switch
        {
            ((int) PixelType.BlackWhite, 1, 1) => (ImagePixelFormat.BW1, SubPixelType.Bit),
            ((int) PixelType.Gray, 8, 1) => (ImagePixelFormat.Gray8, SubPixelType.Gray),
            ((int) PixelType.RGB, 24, 3) => (ImagePixelFormat.RGB24, SubPixelType.Rgb),
            _ => (ImagePixelFormat.Unknown, null)
        };
    }

    private static ImageFileFormat MapFileFormat(FileFormat format) => format switch
    {
        FileFormat.Tiff => ImageFileFormat.Tiff,
        FileFormat.TiffMulti => ImageFileFormat.Tiff,
        FileFormat.Png => ImageFileFormat.Png,
        FileFormat.Jfif => ImageFileFormat.Jpeg,
        _ => ImageFileFormat.Unknown
    };

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TwainRawScanSink));
    }
}

internal sealed class TwainRawTransferCompletionUnavailableException : NotSupportedException
{
    public TwainRawTransferCompletionUnavailableException(string message) : base(message)
    {
    }

    public TwainRawTransferCompletionUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
#endif
