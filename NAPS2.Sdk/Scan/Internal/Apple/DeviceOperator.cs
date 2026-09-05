#if MACOS
using System.Collections.Immutable;
using System.Threading;
using AppKit;
using CoreGraphics;
using Foundation;
using ImageCaptureCore;
using Microsoft.Extensions.Logging;
using NAPS2.Images.Bitwise;
using NAPS2.Images.Mac;
using NAPS2.Scan.Exceptions;

namespace NAPS2.Scan.Internal.Apple;

internal class DeviceOperator : ICScannerDeviceDelegate
{
    private readonly ScanningContext _scanningContext;
    private readonly ILogger _logger;
    private readonly ICScannerDevice _device;
    private ICScannerFunctionalUnit? _unit;
    private nuint _resolution;
    private readonly DeviceReader _reader;
    private readonly ScanOptions _options;
    private readonly IScanEvents _scanEvents;
    private readonly Action<IMemoryImage>? _callback;
    private readonly IRawScanSink? _rawSink;
    private readonly object _rawGate = new();
    private readonly SemaphoreSlim _rawWriterOperationGate = new(1, 1);
    private readonly HashSet<IRawScanArtifactWriter> _rawWriters = new();
    private readonly List<Task> _rawCompletionTasks = new();
    private Task _lastRawCompletionTask = Task.CompletedTask;
    private Task _rawFailureCleanupTask = Task.CompletedTask;
    private Exception? _rawFailure;
    private readonly CancellationToken _rawCancellationToken;
    private bool _rawMode;
    private IRawScanArtifactWriter? _rawWriter;
    private RawScanArtifactHeader? _rawHeader;
    private long _rawBytes;
    private int _rawPageIndex;
    private string? _rawColorSyncProfile;
    private readonly TaskCompletionSource _openSessionTcs = new();
    private readonly TaskCompletionSource _readyTcs = new();
    private TaskCompletionSource<ICScannerFunctionalUnit> _unitTcs = new();
    private readonly TaskCompletionSource _scanSuccessTcs = new();
    private readonly TaskCompletionSource _scanCompleteTcs = new();
    private TaskCompletionSource? _cancelTcs;
    private readonly TaskCompletionSource _closeTcs = new();
    private Task? _writeToCallback;
    private MemoryStream? _buffer;

    public DeviceOperator(ScanningContext scanningContext, ICScannerDevice device, DeviceReader reader,
        ScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents, Action<IMemoryImage>? callback,
        IRawScanSink? rawSink = null)
    {
        _scanningContext = scanningContext;
        _logger = scanningContext.Logger;
        _device = device;
        _reader = reader;
        _options = options;
        _scanEvents = scanEvents;
        _callback = callback;
        _rawSink = rawSink;
        _rawMode = rawSink != null;
        _rawCancellationToken = cancelToken;

        cancelToken.Register(() =>
        {
            _openSessionTcs.TrySetCanceled();
            _readyTcs.TrySetCanceled();
            _unitTcs.TrySetCanceled();
            _scanSuccessTcs.TrySetCanceled();
            _closeTcs.TrySetCanceled();
        });
    }

    public DeviceOperator(ScanningContext scanningContext, ICScannerDevice device, DeviceReader reader,
        RawScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents, IRawScanSink sink)
        : this(scanningContext, device, reader, ToScanOptions(options), cancelToken, scanEvents, null, sink)
    {
    }

    public override void DidOpenSession(ICDevice device, NSError? error)
    {
        try
        {
            _logger.LogDebug("DidOpenSession {Error}", error);
            SetResultOrError(_openSessionTcs, error);
        }
        catch (Exception ex)
        {
            HandleNativeCallbackException(ex);
        }
    }

    public override void DidBecomeReady(ICDevice device)
    {
        try
        {
            _logger.LogDebug("DidBecomeReady");
            _readyTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            HandleNativeCallbackException(ex);
        }
    }

    public override void DidCloseSession(ICDevice device, NSError? error)
    {
        try
        {
            _logger.LogDebug("DidCloseSession {Error}", error);
            SetResultOrError(_closeTcs, error);
        }
        catch (Exception ex)
        {
            HandleNativeCallbackException(ex);
        }
    }

    public override void DidReceiveStatusInformation(ICDevice device, NSDictionary<NSString, NSObject> status)
    {
        try
        {
            var state = status[ICStatusNotificationKeys.NotificationKey] as NSString;
            _logger.LogDebug("DidReceiveStatusInformation {State}", state);

            if (state == ICScannerStatus.WarmingUp && !_rawMode)
            {
                _scanEvents.PageStart();
            }
            if (_cancelTcs != null && _unit?.ScanInProgress != true)
            {
                _cancelTcs.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            HandleNativeCallbackException(ex);
        }
    }

    public override void DidEncounterError(ICDevice device, NSError? error)
    {
        try
        {
            _logger.LogDebug("DidEncounterError {Error}", error);
            var ex = error != null ? new DeviceException(error.Description) : new DeviceException();
            if (_rawMode)
            {
                RecordRawFailure(ex);
            }
            // TODO: Put these in a list or something
            _openSessionTcs.TrySetException(ex);
            _readyTcs.TrySetException(ex);
            _unitTcs.TrySetException(ex);
            _scanSuccessTcs.TrySetException(ex);
            _scanCompleteTcs.TrySetException(ex);
            _closeTcs.TrySetException(ex);
        }
        catch (Exception ex)
        {
            HandleNativeCallbackException(ex);
        }
    }

    // TODO: This will be called if the scanner is in use. We can consider waiting a couple seconds for the scanner
    // TODO: to become available before sending a busy error.
    public override void DidBecomeAvailable(ICScannerDevice scanner)
    {
        _logger.LogDebug("DidBecomeAvailable");
    }

    public override void DidSelectFunctionalUnit(
        ICScannerDevice scanner, ICScannerFunctionalUnit functionalUnit, NSError? error)
    {
        try
        {
            _logger.LogDebug("DidSelectFunctionalUnit {Unit} {Error}", functionalUnit.GetType().Name, error);
            SetResultOrError(_unitTcs, functionalUnit, error);
        }
        catch (Exception ex)
        {
            HandleNativeCallbackException(ex);
        }
    }

    public override void DidScanToBandData(ICScannerDevice scanner, ICScannerBandData data)
    {
        if (_rawMode)
        {
            try
            {
                DidScanToRawBandData(data);
            }
            catch (Exception ex)
            {
                // ImageCaptureCore invokes this method from native code. Never allow a managed exception to cross
                // that callback boundary; record the failure and stop the scan instead.
                RecordRawFailure(ex);
            }
            return;
        }

        var expectedBufferLength = (int) (data.FullImageHeight * data.BytesPerRow);
        _buffer ??= new MemoryStream(expectedBufferLength);
        data.DataBuffer!.AsStream().CopyTo(_buffer);
        // TODO: The buffer gets written pretty much all at once, at least for escl - maybe we can/should reuse TwainProgressEstimator
        _scanEvents.PageProgress(_buffer.Length / (double) expectedBufferLength);

        if (_buffer.Length >= expectedBufferLength)
        {
            _logger.LogDebug("DidScanToBandData buffer complete");
            var fullBuffer = _buffer;
            _buffer = null;
            var tcs = new TaskCompletionSource<IMemoryImage?>();
            // Ensure sequencing is maintained when writing to the callback even if copy tasks finish out of order
            var previousCallback = _writeToCallback ?? Task.CompletedTask;
            _writeToCallback = Task.Run(async () =>
            {
                await previousCallback;
                var image = await tcs.Task;
                if (image != null)
                {
                    _callback!(image);
                }
            });
            Task.Run(() =>
            {
                try
                {
                    // We prefer to use the provided color profile for maximum color accuracy. If one isn't present we
                    // fall back to a direct bitwise copy if it's in a supported pixel format.
                    var (pixelFormat, subPixelType) =
                        (data.PixelDataType, data.NumComponents, data.BitsPerComponent) switch
                        {
                            (ICScannerPixelDataType.BW, 1, 1) => (ImagePixelFormat.BW1, SubPixelType.Bit),
                            (ICScannerPixelDataType.Gray, 1, 8) => (ImagePixelFormat.Gray8, SubPixelType.Gray),
                            (ICScannerPixelDataType.Rgb, 3, 8) => (ImagePixelFormat.RGB24, SubPixelType.Rgb),
                            (ICScannerPixelDataType.Rgb, 4, 8) => (ImagePixelFormat.RGB24, SubPixelType.Rgbn),
                            _ => (ImagePixelFormat.Unknown, null)
                        };
                    _logger.LogDebug(
                        "Image data: width {Width}, height {Height}, type {Type}, comp {Comp}, " +
                        "bits/comp {BitsPerComp}, bits/pixel {BitsPerPixel}, bytes/row {BytesPerRow}, data len {DataLen}",
                        data.FullImageWidth, data.FullImageHeight, data.PixelDataType, data.NumComponents,
                        data.BitsPerComponent, data.BitsPerPixel, data.BytesPerRow, fullBuffer.Length);
                    if (data.ColorSyncProfilePath != null)
                    {
                        _logger.LogDebug($"Flushing image with color sync profile {data.ColorSyncProfilePath}");
                        FlushImageWithColorSpace(tcs, fullBuffer, data, subPixelType);
                    }
                    else if (pixelFormat != ImagePixelFormat.Unknown && subPixelType != null)
                    {
                        _logger.LogDebug($"Flushing image with pixel format {pixelFormat}");
                        FlushImageDirectly(tcs, fullBuffer, data, subPixelType, pixelFormat);
                    }
                    else
                    {
                        _logger.LogError(
                            "No color sync profile and unsupported ICC pixel format " +
                            "{PixelDataType} {NumComponents} {BitsPerComponent}",
                            data.PixelDataType, data.NumComponents, data.BitsPerComponent);
                    }
                    _scanEvents.PageStart();
                }
                finally
                {
                    tcs.TrySetResult(null);
                }
            });
        }
    }

    private void DidScanToRawBandData(ICScannerBandData data)
    {
        ThrowIfRawFailed();
        _rawCancellationToken.ThrowIfCancellationRequested();
        var dataBuffer = data.DataBuffer;
        if (dataBuffer == null || dataBuffer.Length == 0)
        {
            _logger.LogDebug("ICC: Received an empty raw band");
            return;
        }

        var bytes = dataBuffer.ToArray();
        if (bytes.Length == 0)
        {
            return;
        }

        IRawScanArtifactWriter writer;
        RawScanArtifactHeader header;
        long offset;
        int pageIndex;
        lock (_rawGate)
        {
            ThrowIfRawFailedNoLock();
            if (_rawWriter == null)
            {
                _scanEvents.PageStart();
                _rawHeader = CreateRawHeader(data);
                writer = _rawSink!.BeginArtifact(_rawHeader);
                _rawWriter = writer;
                _rawWriters.Add(writer);
                _rawBytes = 0;
                _rawColorSyncProfile = CopyColorSyncProfile(data.ColorSyncProfilePath);
            }
            else
            {
                writer = _rawWriter;
            }
            header = _rawHeader!;
            offset = _rawBytes;
            pageIndex = _rawPageIndex;
        }

        var bytesPerRow = (int) data.BytesPerRow;
        var bandHeight = data.DataNumRows > 0
            ? (int) data.DataNumRows
            : bytesPerRow > 0
                ? (int) Math.Min(int.MaxValue, (bytes.Length + (long) bytesPerRow - 1) / bytesPerRow)
                : (int?) null;
        var shouldFinish = false;
        double? progress = null;
        _rawWriterOperationGate.Wait();
        try
        {
            lock (_rawGate)
            {
                ThrowIfRawFailedNoLock();
                if (!_rawWriters.Contains(writer))
                {
                    throw new DeviceException("Apple raw artifact is no longer available.");
                }
            }
            writer.Write(
                bytes,
                new RawBlockLayout
                {
                    Offset = offset,
                    Width = (int) data.FullImageWidth,
                    Height = bandHeight,
                    Stride = bytesPerRow > 0 ? bytesPerRow : null,
                    BitsPerPixel = (int) data.BitsPerPixel,
                    BytesPerPixel = data.BitsPerPixel >= 8 ? (int) data.BitsPerPixel / 8 : null,
                    PixelFormat = header.PixelFormat,
                    SubPixelType = header.SubPixelType,
                    FrameType = header.FrameType,
                    PageIndex = pageIndex,
                    FrameIndex = 0
                });

            lock (_rawGate)
            {
                _rawBytes += bytes.Length;
                var expectedBytes = header.Height is { } height && header.Stride is { } stride
                    ? checked((long) height * stride)
                    : 0;
                if (expectedBytes > 0)
                {
                    if (_rawBytes > expectedBytes)
                    {
                        throw new DeviceException("Apple returned more raw data than the declared image layout.");
                    }
                    progress = Math.Min(1, _rawBytes / (double) expectedBytes);
                    shouldFinish = _rawBytes == expectedBytes;
                }
            }
        }
        finally
        {
            _rawWriterOperationGate.Release();
        }

        if (progress is { } value)
        {
            _scanEvents.PageProgress(value);
        }
        if (shouldFinish)
        {
            FinishRawPage();
        }
    }

    private RawScanArtifactHeader CreateRawHeader(ICScannerBandData data)
    {
        var (pixelFormat, subPixelType, frameType) = GetPixelDetails(data);
        return new RawScanArtifactHeader
        {
            Type = RawScanArtifactType.AppleBand,
            ContentType = "application/vnd.naps2.apple-raw",
            FileExtension = ".apple-raw",
            PixelFormat = pixelFormat,
            SubPixelType = subPixelType,
            FrameType = frameType,
            Width = (int) data.FullImageWidth,
            Height = (int) data.FullImageHeight,
            Stride = (int) data.BytesPerRow,
            HorizontalResolution = (double) _resolution,
            VerticalResolution = (double) _resolution,
            PageCount = 1,
            SourceId = _device.Uuid
        };
    }

    private RawScanArtifactMetadata CreateRawMetadata()
    {
        var header = _rawHeader!;
        var additionalMetadata = new Dictionary<string, string?>
        {
            [AppleRawScanDecoder.ColorSyncProfileKey] = _rawColorSyncProfile,
            [AppleRawScanDecoder.PixelDataTypeKey] = GetRawPixelDataType(header.PixelFormat),
            [AppleRawScanDecoder.BitsPerComponentKey] = GetRawBitsPerComponent(header).ToString(),
            [AppleRawScanDecoder.BitsPerPixelKey] = GetRawBitsPerPixel(header).ToString(),
            [AppleRawScanDecoder.NumComponentsKey] = GetRawNumComponents(header).ToString(),
            [AppleRawScanDecoder.BytesPerRowKey] = header.Stride?.ToString(),
            [AppleRawScanDecoder.PageIndexKey] = _rawPageIndex.ToString()
        };
        return new RawScanArtifactMetadata
        {
            ByteLength = _rawBytes,
            Width = header.Width,
            Height = header.Height,
            HorizontalResolution = header.HorizontalResolution,
            VerticalResolution = header.VerticalResolution,
            PixelFormat = header.PixelFormat,
            SubPixelType = header.SubPixelType,
            FrameType = header.FrameType,
            PageCount = 1,
            FrameCount = 1,
            PageSide = RawScanPageSide.Unknown,
            IsDuplex = _options.PaperSource == PaperSource.Duplex,
            DeviceId = _device.Uuid,
            SourceId = _device.Uuid,
            ContentType = header.ContentType,
            AdditionalMetadata = additionalMetadata
        };
    }

    private static (ImagePixelFormat PixelFormat, SubPixelType? SubPixelType, RawScanFrameType FrameType)
        GetPixelDetails(ICScannerBandData data)
    {
        return (data.PixelDataType, data.NumComponents, data.BitsPerComponent) switch
        {
            (ICScannerPixelDataType.BW, 1, 1) =>
                (ImagePixelFormat.BW1, SubPixelType.Bit, RawScanFrameType.Gray),
            (ICScannerPixelDataType.Gray, 1, 8) =>
                (ImagePixelFormat.Gray8, SubPixelType.Gray, RawScanFrameType.Gray),
            (ICScannerPixelDataType.Rgb, 3, 8) =>
                (ImagePixelFormat.RGB24, SubPixelType.Rgb, RawScanFrameType.Image),
            (ICScannerPixelDataType.Rgb, 4, 8) =>
                (ImagePixelFormat.RGB24, SubPixelType.Rgbn, RawScanFrameType.Image),
            _ => (ImagePixelFormat.Unknown, null, RawScanFrameType.Unknown)
        };
    }

    private static string GetRawPixelDataType(ImagePixelFormat pixelFormat) => pixelFormat switch
    {
        ImagePixelFormat.BW1 => nameof(ICScannerPixelDataType.BW),
        ImagePixelFormat.Gray8 => nameof(ICScannerPixelDataType.Gray),
        ImagePixelFormat.RGB24 or ImagePixelFormat.ARGB32 => nameof(ICScannerPixelDataType.Rgb),
        _ => "Unknown"
    };

    private static int GetRawBitsPerComponent(RawScanArtifactHeader header) => header.PixelFormat switch
    {
        ImagePixelFormat.BW1 => 1,
        _ => 8
    };

    private static int GetRawBitsPerPixel(RawScanArtifactHeader header) => header.SubPixelType?.BitsPerPixel ??
                                                                          (header.PixelFormat switch
                                                                          {
                                                                              ImagePixelFormat.BW1 => 1,
                                                                              ImagePixelFormat.Gray8 => 8,
                                                                              ImagePixelFormat.RGB24 => 24,
                                                                              ImagePixelFormat.ARGB32 => 32,
                                                                              _ => 0
                                                                          });

    private static int GetRawNumComponents(RawScanArtifactHeader header) => header.PixelFormat switch
    {
        ImagePixelFormat.BW1 or ImagePixelFormat.Gray8 => 1,
        ImagePixelFormat.RGB24 when header.SubPixelType == SubPixelType.Rgbn => 4,
        ImagePixelFormat.RGB24 => 3,
        ImagePixelFormat.ARGB32 => 4,
        _ => 0
    };

    private string? CopyColorSyncProfile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            return Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICC: Could not copy ColorSync profile {Path}", path);
            return null;
        }
    }

    private void FlushImageDirectly(TaskCompletionSource<IMemoryImage?> tcs, MemoryStream fullBuffer,
        ICScannerBandData data, SubPixelType subPixelType,
        ImagePixelFormat pixelFormat)
    {
        var image = _scanningContext.ImageContext.Create(
            (int) data.FullImageWidth, (int) data.FullImageHeight, pixelFormat);
        var bufferInfo = new PixelInfo(
            (int) data.FullImageWidth,
            (int) data.FullImageHeight,
            subPixelType!,
            (int) data.BytesPerRow);
        _buffer ??= new MemoryStream((int) bufferInfo.Length);
        new CopyBitwiseImageOp().Perform(fullBuffer.GetBuffer(), bufferInfo, image);
        _logger.LogDebug("Setting resolution to {Dpi}", _resolution);
        image.SetResolution(_resolution, _resolution);
        tcs.SetResult(image);
    }

    private void FlushImageWithColorSpace(TaskCompletionSource<IMemoryImage?> tcs, MemoryStream fullBuffer,
        ICScannerBandData data, SubPixelType? subPixelType)
    {
        var colorSpace = CGColorSpace.CreateIccData(NSData.FromFile(data.ColorSyncProfilePath!));
        var w = (int) data.FullImageWidth;
        var h = (int) data.FullImageHeight;
        var bitsPerComponent = (int) data.BitsPerComponent;
        var bitsPerPixel = (int) data.BitsPerPixel;
        var bytesPerRow = (int) data.BytesPerRow;
        var flags = (bitsPerPixel == 32 ? CGBitmapFlags.NoneSkipLast : CGBitmapFlags.None) |
                    CGBitmapFlags.ByteOrderDefault;
        var buffer = fullBuffer.GetBuffer();
        var dataProvider = new CGDataProvider(buffer, 0, buffer.Length);

        // There is an apparent bug in ImageCaptureCore where grayscale images can report a bytesPerRow value that is
        // aligned to a word boundary (and the buffer is sized to match), but the actual data is stored as if that
        // wasn't the case, leaving a block of zeros at the end of the buffer. We correct for this here.
        // TODO: Is there any case where this will backfire? Can we detect the problem (e.g. by checking for zeros at
        // the end of the buffer)?
        if (subPixelType == SubPixelType.Gray)
        {
            bytesPerRow = w;
        }

        var cgImage = new CGImage(w, h, bitsPerComponent, bitsPerPixel, bytesPerRow, colorSpace, flags,
            dataProvider, null, true, CGColorRenderingIntent.Default);
        var imageRep = new NSBitmapImageRep(cgImage);
        var nsImage = new NSImage();
        nsImage.AddRepresentation(imageRep);
        // TODO: Could maybe do this without the NAPS2.Images.Mac reference but that would require duplicating
        // a bunch of logic to normalize image reps etc.
        var macImage = new MacImage(nsImage);
        _logger.LogDebug("Setting resolution to {Dpi}", _resolution);
        macImage.SetResolution(_resolution, _resolution);
        if (_scanningContext.ImageContext is MacImageContext)
        {
            tcs.SetResult(macImage);
        }
        else
        {
            var image = macImage.Copy(_scanningContext.ImageContext);
            macImage.Dispose();
            tcs.SetResult(image);
        }
    }

    public override void DidCompleteScan(ICScannerDevice scanner, NSError? error)
    {
        try
        {
            _logger.LogDebug("DidCompleteScan {Error}", error);
            if (_rawMode && error != null)
            {
                RecordRawFailure(GetException(error));
            }
            SetResultOrError(_scanSuccessTcs, error);
            SetResultOrError(_scanCompleteTcs, error);
        }
        catch (Exception ex)
        {
            HandleNativeCallbackException(ex);
        }
    }

    private void SetResultOrError(TaskCompletionSource tcs, NSError? error)
    {
        if (error != null)
        {
            tcs.TrySetException(GetException(error));
        }
        else
        {
            tcs.TrySetResult();
        }
    }

    private void SetResultOrError<T>(TaskCompletionSource<T> tcs, T value, NSError? error)
    {
        if (error != null)
        {
            tcs.TrySetException(GetException(error));
        }
        else
        {
            tcs.TrySetResult(value);
        }
    }

    private Exception GetException(NSError error)
    {
        return new DeviceException(error.LocalizedDescription);
    }

    public override void DidRemoveDevice(ICDevice device)
    {
    }

    public async Task<ScanCaps> GetCaps()
    {
        try
        {
            _device.Delegate = this;
            _logger.LogDebug("ICC: Opening session for caps");
            _device.RequestOpenSession();
            await _openSessionTcs.Task;
            _logger.LogDebug("ICC: Waiting for ready");
            await _readyTcs.Task;

            var unitTypes = _device.AvailableFunctionalUnitTypes;
            bool supportsFeeder = unitTypes.Contains((NSNumber) (int) ICScannerFunctionalUnitType.Flatbed);
            bool supportsFlatbed = unitTypes.Contains((NSNumber) (int) ICScannerFunctionalUnitType.DocumentFeeder);
            bool supportsDuplex = false;
            PerSourceCaps? flatbedCaps = null;
            PerSourceCaps? feederCaps = null;
            PerSourceCaps? duplexCaps = null;

            _logger.LogDebug("ICC: Selecting flatbed unit");
            _unit = await SelectUnit(ICScannerFunctionalUnitType.Flatbed);
            if (_unit is ICScannerFunctionalUnitFlatbed flatbedUnit)
            {
                flatbedCaps = GetCapsFromUnit(flatbedUnit);
            }

            _logger.LogDebug("ICC: Selecting feeder unit");
            _unit = await SelectUnit(ICScannerFunctionalUnitType.DocumentFeeder);
            if (_unit is ICScannerFunctionalUnitDocumentFeeder feederUnit)
            {
                supportsDuplex = feederUnit.SupportsDuplexScanning;
                feederCaps = GetCapsFromUnit(feederUnit);
                if (supportsDuplex) duplexCaps = feederCaps;
            }

            _logger.LogDebug("ICC: Closing session");
            _device.RequestCloseSession();
            await _closeTcs.Task;
            _logger.LogDebug("ICC: Caps query success");

            return new ScanCaps
            {
                MetadataCaps = new()
                {
                    SerialNumber = _device.SerialNumber
                },
                PaperSourceCaps = new()
                {
                    SupportsFlatbed = supportsFlatbed,
                    SupportsFeeder = supportsFeeder,
                    SupportsDuplex = supportsDuplex,
                    CanCheckIfFeederHasPaper = true
                },
                FlatbedCaps = flatbedCaps,
                FeederCaps = feederCaps,
                DuplexCaps = duplexCaps
            };
        }
        finally
        {
            if (_device.HasOpenSession)
            {
                _logger.LogDebug("ICC: Closing session (in finally)");
                _device.RequestCloseSession();
            }
        }
    }

    private PerSourceCaps GetCapsFromUnit(ICScannerFunctionalUnit unit)
    {
        unit.MeasurementUnit = ICScannerMeasurementUnit.Inches;
        return new PerSourceCaps
        {
            DpiCaps = new()
            {
                Values = unit.SupportedResolutions.Select(x => (int) x).Order().ToImmutableList()
            },
            BitDepthCaps = new()
            {
                SupportsBlackAndWhite = unit.SupportedBitDepths.Contains((nuint) ICScannerBitDepth.Bits1),
                SupportsGrayscale = unit.SupportedBitDepths.Contains((nuint) ICScannerBitDepth.Bits8),
                SupportsColor = unit.SupportedBitDepths.Contains((nuint) ICScannerBitDepth.Bits8)
            },
            PageSizeCaps = new()
            {
                ScanArea = new PageSize(
                    (decimal) unit.PhysicalSize.Width,
                    (decimal) unit.PhysicalSize.Height,
                    PageSizeUnit.Inch)
            }
        };
    }

    public async Task Scan()
    {
        try
        {
            _device.Delegate = this;
            _logger.LogDebug("ICC: Opening session");
            _device.RequestOpenSession();
            await _openSessionTcs.Task;
            _logger.LogDebug("ICC: Waiting for ready");
            await _readyTcs.Task;
            _logger.LogDebug("ICC: Selecting unit");
            _unit = await SelectUnit(_options.PaperSource is PaperSource.Flatbed or PaperSource.Auto
                ? ICScannerFunctionalUnitType.Flatbed
                : ICScannerFunctionalUnitType.DocumentFeeder);
            if (_unit is ICScannerFunctionalUnitDocumentFeeder { SupportsDuplexScanning: true } feederUnit)
            {
                feederUnit.DuplexScanningEnabled = _options.PaperSource == PaperSource.Duplex;
            }
            _logger.LogDebug("ICC: Setting scan parameters");
            SetScanArea(_unit);
            _resolution = GetClosestResolution((nuint) _options.Dpi, _unit);
            _unit.Resolution = _resolution;
            _unit.BitDepth = _options.BitDepth == BitDepth.BlackAndWhite
                ? ICScannerBitDepth.Bits1
                : ICScannerBitDepth.Bits8;
            _unit.PixelDataType = _options.BitDepth switch
            {
                BitDepth.BlackAndWhite => ICScannerPixelDataType.BW,
                BitDepth.Grayscale => ICScannerPixelDataType.Gray,
                _ => ICScannerPixelDataType.Rgb
            };
            _device.TransferMode = ICScannerTransferMode.MemoryBased;
            _device.MaxMemoryBandSize = 65536;
            _logger.LogDebug("ICC: Requesting scan");
            _device.RequestScan();
            await _scanSuccessTcs.Task;
            if (_writeToCallback == null && _unit is ICScannerFunctionalUnitDocumentFeeder { DocumentLoaded: false })
            {
                _logger.LogDebug("ICC: No pages in feeder");
                throw new DeviceFeederEmptyException();
            }
            if (_writeToCallback != null)
            {
                _logger.LogDebug("ICC: Waiting for scan results");
                await _writeToCallback;
            }
            _logger.LogDebug("ICC: Closing session");
            _device.RequestCloseSession();
            await _closeTcs.Task;
            _logger.LogDebug("ICC: Scan success");
        }
        catch (TaskCanceledException)
        {
            if (_unit != null && _unit.ScanInProgress)
            {
                _cancelTcs = new TaskCompletionSource();
                _logger.LogDebug("ICC: Cancelling scan");
                _device.CancelScan();
                await Task.WhenAny(_scanCompleteTcs.Task, _cancelTcs.Task);
            }
            _logger.LogDebug("ICC: Scan cancelled");
        }
        finally
        {
            if (_device.HasOpenSession)
            {
                _logger.LogDebug("ICC: Closing session (in finally)");
                _device.RequestCloseSession();
            }
        }
    }

    public async Task ScanRaw()
    {
        try
        {
            _device.Delegate = this;
            _logger.LogDebug("ICC: Opening session for raw acquisition");
            _device.RequestOpenSession();
            await _openSessionTcs.Task;
            _logger.LogDebug("ICC: Waiting for ready");
            await _readyTcs.Task;
            _logger.LogDebug("ICC: Selecting unit");
            _unit = await SelectUnit(_options.PaperSource is PaperSource.Flatbed or PaperSource.Auto
                ? ICScannerFunctionalUnitType.Flatbed
                : ICScannerFunctionalUnitType.DocumentFeeder);
            if (_unit is ICScannerFunctionalUnitDocumentFeeder { SupportsDuplexScanning: true } feederUnit)
            {
                feederUnit.DuplexScanningEnabled = _options.PaperSource == PaperSource.Duplex;
            }
            _logger.LogDebug("ICC: Setting raw scan parameters");
            SetScanArea(_unit);
            _resolution = GetClosestResolution((nuint) _options.Dpi, _unit);
            _unit.Resolution = _resolution;
            _unit.BitDepth = _options.BitDepth == BitDepth.BlackAndWhite
                ? ICScannerBitDepth.Bits1
                : ICScannerBitDepth.Bits8;
            _unit.PixelDataType = _options.BitDepth switch
            {
                BitDepth.BlackAndWhite => ICScannerPixelDataType.BW,
                BitDepth.Grayscale => ICScannerPixelDataType.Gray,
                _ => ICScannerPixelDataType.Rgb
            };
            _device.TransferMode = ICScannerTransferMode.MemoryBased;
            _device.MaxMemoryBandSize = 65536;
            _rawSink!.ConfigurationApplied(new DriverProcessingResult());
            _logger.LogDebug("ICC: Requesting raw scan");
            _device.RequestScan();
            await _scanSuccessTcs.Task;
            ThrowIfRawFailed();
            // Most scans complete the artifact when FullImageHeight is reached. The completion callback is also the
            // authoritative boundary for devices that report an unknown or padded height.
            FinishRawPage();
            await DrainRawArtifactsAsync(propagateErrors: true);
            if (GetRawCompletionTaskCount() == 0 &&
                _unit is ICScannerFunctionalUnitDocumentFeeder { DocumentLoaded: false })
            {
                _logger.LogDebug("ICC: No pages in feeder");
                throw new DeviceFeederEmptyException();
            }
            _logger.LogDebug("ICC: Closing raw scan session");
            _device.RequestCloseSession();
            await _closeTcs.Task;
            _logger.LogDebug("ICC: Raw scan success");
        }
        catch (TaskCanceledException ex)
        {
            RecordRawFailure(ex);
            await CancelRawScanAndWait();
            await AbortRawPage();
            await DrainRawArtifactsAsync();
            _logger.LogDebug("ICC: Raw scan cancelled");
        }
        catch (Exception ex)
        {
            RecordRawFailure(ex);
            await CancelRawScanAndWait();
            await AbortRawPage();
            await DrainRawArtifactsAsync();
            throw;
        }
        finally
        {
            if (_rawMode)
            {
                try
                {
                    await AbortRawPage();
                    await DrainRawArtifactsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ICC: Error draining raw artifact writers");
                }
            }
            if (_device.HasOpenSession)
            {
                _logger.LogDebug("ICC: Closing raw session (in finally)");
                _device.RequestCloseSession();
            }
        }
    }

    private void FinishRawPage()
    {
        IRawScanArtifactWriter writer;
        RawScanArtifactMetadata metadata;
        Task previousCompletion;
        lock (_rawGate)
        {
            if (_rawWriter == null || _rawHeader == null)
            {
                return;
            }

            ThrowIfRawFailedNoLock();
            var expectedBytes = GetExpectedRawBytes(_rawHeader);
            if (expectedBytes > 0 && _rawBytes != expectedBytes)
            {
                throw new DeviceException(
                    $"Apple returned {_rawBytes} raw bytes for an image that declares {expectedBytes} bytes.");
            }

            writer = _rawWriter;
            metadata = CreateRawMetadata();
            previousCompletion = _lastRawCompletionTask;
            _rawWriter = null;
            _rawHeader = null;
            _rawBytes = 0;
            _rawColorSyncProfile = null;
            _rawPageIndex++;
        }

        var completionTask = CompleteAndDisposeRawWriter(writer, metadata, previousCompletion);
        lock (_rawGate)
        {
            _lastRawCompletionTask = completionTask;
            _rawCompletionTasks.Add(completionTask);
        }
    }

    private async Task AbortRawPage()
    {
        IRawScanArtifactWriter? writer;
        lock (_rawGate)
        {
            writer = _rawWriter;
            if (writer == null)
            {
                return;
            }

            _rawWriter = null;
            _rawHeader = null;
            _rawBytes = 0;
            _rawColorSyncProfile = null;
        }

        await AbortAndDisposeTrackedRawWriter(writer);
    }

    private async Task CompleteAndDisposeRawWriter(IRawScanArtifactWriter writer,
        RawScanArtifactMetadata metadata, Task previousCompletion)
    {
        try
        {
            await previousCompletion;
            await _scanSuccessTcs.Task;
            _rawCancellationToken.ThrowIfCancellationRequested();

            var ownsWriter = false;
            await _rawWriterOperationGate.WaitAsync();
            try
            {
                lock (_rawGate)
                {
                    if (!_rawWriters.Contains(writer))
                    {
                        return;
                    }
                    ThrowIfRawFailedNoLock();
                }
                _rawCancellationToken.ThrowIfCancellationRequested();
                await writer.CompleteAsync(metadata, _rawCancellationToken);
                ThrowIfRawFailed();
                lock (_rawGate)
                {
                    ownsWriter = _rawWriters.Remove(writer);
                }
            }
            finally
            {
                _rawWriterOperationGate.Release();
            }

            if (ownsWriter)
            {
                await writer.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            RecordRawFailure(ex);
            await AbortAndDisposeTrackedRawWriter(writer);
            throw;
        }
    }

    private async Task AbortAndDisposeTrackedRawWriter(IRawScanArtifactWriter writer)
    {
        var ownsWriter = false;
        try
        {
            await _rawWriterOperationGate.WaitAsync();
            try
            {
                lock (_rawGate)
                {
                    ownsWriter = _rawWriters.Remove(writer);
                }
                if (ownsWriter)
                {
                    await writer.AbortAsync(CancellationToken.None);
                }
            }
            finally
            {
                _rawWriterOperationGate.Release();
            }
        }
        catch (Exception ex)
        {
            // Cleanup must not mask the acquisition or completion failure that caused it.
            _logger.LogDebug(ex, "ICC: Error aborting raw artifact writer");
        }
        finally
        {
            if (ownsWriter)
            {
                try
                {
                    await writer.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ICC: Error disposing raw artifact writer");
                }
            }
        }
    }

    private async Task CancelRawScanAndWait()
    {
        try
        {
            if (_unit?.ScanInProgress != true)
            {
                return;
            }

            _cancelTcs = new TaskCompletionSource();
            _logger.LogDebug("ICC: Cancelling raw scan");
            try
            {
                _device.CancelScan();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ICC: Error cancelling raw scan");
            }
            await Task.WhenAny(_scanCompleteTcs.Task, _cancelTcs.Task);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICC: Error waiting for raw scan cancellation");
        }
    }

    private Task DrainRawArtifactsAsync(bool propagateErrors = false)
    {
        return DrainRawArtifactsCoreAsync(propagateErrors);
    }

    private async Task DrainRawArtifactsCoreAsync(bool propagateErrors)
    {
        Task cleanupTask;
        lock (_rawGate)
        {
            cleanupTask = _rawFailureCleanupTask;
        }
        try
        {
            await cleanupTask;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICC: Error aborting raw artifact writers");
        }

        Exception? firstCompletionError = null;
        while (true)
        {
            Task[] completionTasks;
            lock (_rawGate)
            {
                completionTasks = _rawCompletionTasks.ToArray();
            }
            if (completionTasks.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(completionTasks);
            }
            catch (Exception ex)
            {
                firstCompletionError ??= ex;
                _logger.LogDebug(ex, "ICC: Error completing raw artifact writers");
            }

            lock (_rawGate)
            {
                if (_rawCompletionTasks.Count == completionTasks.Length)
                {
                    if (propagateErrors && firstCompletionError != null)
                    {
                        throw firstCompletionError;
                    }
                    return;
                }
            }
        }
    }

    private int GetRawCompletionTaskCount()
    {
        lock (_rawGate)
        {
            return _rawCompletionTasks.Count;
        }
    }

    private static long GetExpectedRawBytes(RawScanArtifactHeader header) =>
        header.Height is { } height && header.Stride is { } stride && height > 0 && stride > 0
            ? checked((long) height * stride)
            : 0;

    private void RecordRawFailure(Exception exception)
    {
        TaskCompletionSource? cleanupStarted = null;
        lock (_rawGate)
        {
            if (_rawFailure != null)
            {
                return;
            }

            _rawFailure = exception;
            cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _rawFailureCleanupTask = cleanupStarted.Task;
        }

        try
        {
            _scanSuccessTcs.TrySetException(exception);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICC: Error recording raw scan failure");
        }

        try
        {
            _device.CancelScan();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICC: Error stopping failed raw scan");
        }

        _ = StartRawFailureCleanupAsync(cleanupStarted!);
    }

    private async Task StartRawFailureCleanupAsync(TaskCompletionSource cleanupStarted)
    {
        try
        {
            await AbortTrackedRawWritersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ICC: Error cleaning up raw artifact writers");
        }
        finally
        {
            cleanupStarted.TrySetResult();
        }
    }

    private async Task AbortTrackedRawWritersAsync()
    {
        IRawScanArtifactWriter[] writers;
        lock (_rawGate)
        {
            writers = _rawWriters.ToArray();
        }
        foreach (var writer in writers)
        {
            await AbortAndDisposeTrackedRawWriter(writer);
        }
    }

    private void ThrowIfRawFailed()
    {
        lock (_rawGate)
        {
            ThrowIfRawFailedNoLock();
        }
    }

    private void ThrowIfRawFailedNoLock()
    {
        if (_rawFailure is { } failure)
        {
            throw new InvalidOperationException("Apple raw scan has failed.", failure);
        }
    }

    private void HandleNativeCallbackException(Exception exception)
    {
        if (_rawMode)
        {
            RecordRawFailure(exception);
        }
        else
        {
            _logger.LogDebug(exception, "ICC: Native callback failed");
        }
    }

    private static ScanOptions ToScanOptions(RawScanOptions options) => new()
    {
        Driver = options.Driver,
        Device = options.Device,
        PaperSource = options.PaperSource,
        Dpi = options.Dpi,
        PageSize = options.PageSize,
        BitDepth = options.BitDepth,
        PageAlign = options.PageAlign,
        UseNativeUI = options.UseNativeUI,
        DialogParent = options.DialogParent
    };

    private ICScannerDocumentType GetDocumentTypeFromPageSize(PageSize? pageSize)
    {
        // TODO: Maybe some tolerance, e.g. if translating over EsclScanServer?
        if (pageSize == PageSize.A3) return ICScannerDocumentType.A3;
        if (pageSize == PageSize.A4) return ICScannerDocumentType.A4;
        if (pageSize == PageSize.A5) return ICScannerDocumentType.A5;
        if (pageSize == PageSize.Letter) return ICScannerDocumentType.USLetter;
        if (pageSize == PageSize.Legal) return ICScannerDocumentType.USLegal;
        if (pageSize == PageSize.B4) return ICScannerDocumentType.IsoB4;
        if (pageSize == PageSize.B5) return ICScannerDocumentType.IsoB5;
        return ICScannerDocumentType.Default;
    }

    private nuint GetClosestResolution(nuint dpi, ICScannerFunctionalUnit unit)
    {
        var targetDpi = dpi;
        if (unit.SupportedResolutions.Count > 0)
        {
            targetDpi = unit.SupportedResolutions.MinBy(x => Math.Abs((int) (x - dpi)));
        }
        if (targetDpi != dpi)
        {
            _logger.LogDebug("ICC: Correcting resolution from {InDpi} to {OutDpi}", dpi, targetDpi);
        }
        return targetDpi;
    }

    private void SetScanArea(ICScannerFunctionalUnit unit)
    {
        // Setting DocumentType should be redundant (setting ScanArea is more general), but it shouldn't hurt and may
        // help with some issues with particular scanners.
        if (_unit is ICScannerFunctionalUnitDocumentFeeder feederUnit)
        {
            feederUnit.DocumentType = GetDocumentTypeFromPageSize(_options.PageSize);
        }
        if (_unit is ICScannerFunctionalUnitFlatbed flatbedUnit)
        {
            flatbedUnit.DocumentType = GetDocumentTypeFromPageSize(_options.PageSize);
        }
        unit.MeasurementUnit = ICScannerMeasurementUnit.Inches;
        var maxSize = unit.PhysicalSize;
        var width = Math.Min((double) _options.PageSize!.WidthInInches, maxSize.Width);
        var height = Math.Min((double) _options.PageSize.HeightInInches, maxSize.Height);
        var deltaX = maxSize.Width - width;
        var offsetX = _options.PageAlign switch
        {
            HorizontalAlign.Left => deltaX,
            HorizontalAlign.Center => deltaX / 2,
            _ => 0
        };
        unit.ScanArea = new CGRect(offsetX, 0, offsetX + width, height);
    }

    private async Task<ICScannerFunctionalUnit> SelectUnit(ICScannerFunctionalUnitType unitType)
    {
        // TODO: Can we clean this up at all?
        var availableUnits = _device.AvailableFunctionalUnitTypes.Select(x =>
            (ICScannerFunctionalUnitType) (int) x).ToList();
        await _unitTcs.Task;
        _unitTcs = new TaskCompletionSource<ICScannerFunctionalUnit>();
        if (availableUnits.Contains(unitType))
        {
            _device.RequestSelectFunctionalUnit(unitType);
            var result = await _unitTcs.Task;
            return result;
        }
        return _device.SelectedFunctionalUnit;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_rawMode)
            {
                bool hasRawWriters;
                lock (_rawGate)
                {
                    hasRawWriters = _rawWriters.Count > 0 || _rawWriter != null;
                }
                if (hasRawWriters || _unit?.ScanInProgress == true)
                {
                    RecordRawFailure(new ObjectDisposedException(nameof(DeviceOperator)));
                }
                try
                {
                    CancelRawScanAndWait().GetAwaiter().GetResult();
                    AbortRawPage().GetAwaiter().GetResult();
                    DrainRawArtifactsAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ICC: Error draining raw artifact writers during dispose");
                }
            }
            _device.Delegate = null;
        }
        base.Dispose(disposing);
    }
}
#endif
