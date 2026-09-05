using System.Collections.Immutable;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using NAPS2.Escl;
using NAPS2.Escl.Client;
using NAPS2.Images;
using NAPS2.Pdf;
using NAPS2.Remoting;
using NAPS2.Scan.Exceptions;
using NAPS2.Serialization;

namespace NAPS2.Scan.Internal.Escl;

internal class EsclScanDriver : IScanDriver
{
    private const int MAX_DOCUMENT_TRIES = 5;
    private const int DOCUMENT_RETRY_INTERVAL = 1000;

    private readonly ScanningContext _scanningContext;
    private readonly ILogger _logger;

    private static string GetUuid(ScanDevice device)
    {
        var parts = device.ID.Split('|');
        if (parts.Length == 1)
        {
            // Current IDs are just the UUID
            return parts[0];
        }
        if (parts.Length != 2)
        {
            throw new ArgumentException("Invalid ESCL device ID");
        }
        // Old IDs have both the IP and UUID separated by "|"
        return parts[1];
    }

    public EsclScanDriver(ScanningContext scanningContext)
    {
        _scanningContext = scanningContext;
        _logger = scanningContext.Logger;
    }

    public async Task GetDevices(ScanOptions options, CancellationToken cancelToken, Action<ScanDevice> callback)
    {
        // TODO: Run location in a persistent background service
        var localIPsTask = options.ExcludeLocalIPs ? LocalIPsHelper.Get() : null;
        using var locator = new EsclServiceLocator(service =>
        {
            // TODO: Consider limiting available devices by security policy
            var ip = service.IpV4 ?? service.IpV6!;
            if (options.ExcludeLocalIPs && localIPsTask!.Result.Contains(ip.ToString()))
            {
                return;
            }
            var id = service.Uuid;
            var name = string.IsNullOrEmpty(service.ScannerName)
                ? $"{ip}"
                : $"{service.ScannerName} ({ip})";
            var client = new EsclClient(service);
            callback(new ScanDevice(Driver.Escl, id, name, client.IconUri, client.ConnectionUri));
        });
        locator.Logger = _logger;
        locator.Start();
        try
        {
            await Task.Delay(options.EsclOptions.SearchTimeout, cancelToken);
        }
        catch (TaskCanceledException)
        {
        }
    }

    public async Task<ScanCaps> GetCaps(ScanOptions options, CancellationToken cancelToken)
    {
        if (cancelToken.IsCancellationRequested) return new ScanCaps();

        try
        {
            var (client, caps) = await GetEsclClientWithCaps(options, cancelToken, ScanEvents.Stub);
            if (client == null || caps == null) return new ScanCaps();
            return new ScanCaps
            {
                MetadataCaps = new()
                {
                    Model = caps.MakeAndModel,
                    Manufacturer = caps.Manufacturer,
                    SerialNumber = caps.SerialNumber,
                    IconUri = client.IconUri
                },
                PaperSourceCaps = new()
                {
                    SupportsFlatbed = caps.PlatenCaps != null,
                    SupportsFeeder = caps.AdfSimplexCaps != null,
                    SupportsDuplex = caps.AdfDuplexCaps != null,
                    CanCheckIfFeederHasPaper = true
                },
                FlatbedCaps = MapCaps(caps.PlatenCaps),
                FeederCaps = MapCaps(caps.AdfSimplexCaps),
                DuplexCaps = MapCaps(caps.AdfDuplexCaps),
                // The current eSCL request mapper does not query or send driver-side processing controls. Keep the
                // capability shape explicit so consumers can distinguish that state from a missing caps object,
                // without advertising support that has not been observed.
                DriverProcessingCaps = CreateUnknownProcessingCaps()
            };
        }
        catch (HttpRequestException ex) when (ex.InnerException is TaskCanceledException or SocketException)
        {
            // A connection timeout manifests as TaskCanceledException
            _logger.LogError(ex, "Error connecting to ESCL device");
            throw new DeviceCommunicationException();
        }
        catch (TaskCanceledException)
        {
        }
        return new ScanCaps();
    }

    private static DriverProcessingCaps CreateUnknownProcessingCaps()
    {
        return new DriverProcessingCaps
        {
            Brightness = new DriverProcessingNumericCaps { State = DriverProcessingCapabilityState.Unknown },
            Contrast = new DriverProcessingNumericCaps { State = DriverProcessingCapabilityState.Unknown },
            RotationDegrees = new DriverProcessingNumericCaps { State = DriverProcessingCapabilityState.Unknown },
            AutomaticOrientation = new DriverProcessingBooleanCaps
            {
                State = DriverProcessingCapabilityState.Unknown
            },
            Deskew = new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.Unknown },
            AutomaticBrightness = new DriverProcessingBooleanCaps
            {
                State = DriverProcessingCapabilityState.Unknown
            },
            AutomaticPageSize = new DriverProcessingBooleanCaps
            {
                State = DriverProcessingCapabilityState.Unknown
            },
            AutomaticBorderDetection = new DriverProcessingBooleanCaps
            {
                State = DriverProcessingCapabilityState.Unknown
            },
            AutomaticCrop = new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.Unknown },
            AutomaticColorDetection = new DriverProcessingColorCaps
            {
                State = DriverProcessingCapabilityState.Unknown
            },
            AutomaticBlankPageDetection = new DriverProcessingBooleanCaps
            {
                State = DriverProcessingCapabilityState.Unknown
            }
        };
    }

    private PerSourceCaps? MapCaps(EsclInputCaps? caps)
    {
        if (caps == null)
        {
            return null;
        }
        return PerSourceCaps.UnionAll(caps.SettingProfiles.Select(profile => MapSettingProfile(caps, profile)));
    }

    private PerSourceCaps MapSettingProfile(EsclInputCaps caps, EsclSettingProfile profile)
    {
        DpiCaps? dpiCaps = null;
        if (profile.DiscreteResolutions.Count > 0)
        {
            dpiCaps = new DpiCaps
            {
                Values = profile.DiscreteResolutions
                    .Where(res => res.XResolution == res.YResolution)
                    .Select(res => res.XResolution).ToImmutableList()
            };
        }
        else if (profile.XResolutionRange != null && profile.YResolutionRange != null)
        {
            int min = Math.Max(profile.XResolutionRange.Min, profile.YResolutionRange.Min);
            int max = Math.Min(profile.XResolutionRange.Max, profile.YResolutionRange.Max);
            int step = Math.Max(profile.XResolutionRange.Step, profile.YResolutionRange.Step);
            dpiCaps = DpiCaps.ForRange(min, max, step);
        }
        return new PerSourceCaps
        {
            DpiCaps = dpiCaps,
            BitDepthCaps = new BitDepthCaps
            {
                SupportsColor = profile.ColorModes.Contains(EsclColorMode.RGB24),
                SupportsGrayscale = profile.ColorModes.Contains(EsclColorMode.Grayscale8),
                SupportsBlackAndWhite = profile.ColorModes.Contains(EsclColorMode.BlackAndWhite1)
            },
            PageSizeCaps = caps.MaxWidth != null && caps.MaxHeight != null
                ? new PageSizeCaps
                {
                    ScanArea = new PageSize(caps.MaxWidth.Value / 300m, caps.MaxHeight.Value / 300m, PageSizeUnit.Inch)
                }
                : null
        };
    }

    public async Task Scan(ScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents,
        Action<IMemoryImage> callback)
    {
        if (cancelToken.IsCancellationRequested) return;

        try
        {
            var (client, caps) = await GetEsclClientWithCaps(options, cancelToken, scanEvents);
            if (client == null || caps == null) return;
            var status = await client.GetStatus();
            bool hasProgressExtension = caps.Naps2Extensions?.Contains("Progress") ?? false;
            bool hasErrorDetailsExtension = caps.Naps2Extensions?.Contains("ErrorDetails") ?? false;
            bool hasShortTimeoutExtension = caps.Naps2Extensions?.Contains("ShortTimeout") ?? false;
            bool hasAnyDpiExtension = caps.Naps2Extensions?.Contains("AnyDpi") ?? false;
            var scanSettings = GetScanSettings(options, caps, hasAnyDpiExtension);
            Action<double>? progressCallback = hasProgressExtension ? scanEvents.PageProgress : null;

            if (cancelToken.IsCancellationRequested) return;

            VerifyStatus(status, scanSettings);

            var (job, effectiveScanSettings) = await CreateScanJobAndCorrectInvalidSettings(client, scanSettings);

            var cancelOnce = new Once(() => client.CancelJob(job).AssertNoAwait());
            using var cancelReg = cancelToken.Register(cancelOnce.Run);

            try
            {
                if (effectiveScanSettings.InputSource == EsclInputSource.Platen)
                {
                    scanEvents.PageStart();
                }
                while (true)
                {
                    if (effectiveScanSettings.InputSource != EsclInputSource.Platen)
                    {
                        scanEvents.PageStart();
                    }
                    var doc = await GetNextDocumentWithRetries(client, job, progressCallback, hasShortTimeoutExtension);
                    if (doc == null) break;
                    foreach (var image in GetImagesFromRawDocument(options, doc))
                    {
                        callback(image);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ESCL driver error");
                cancelOnce.Run();
                // The root cause for the exception might be a server-side scanning error, so prefer to throw a more
                // descriptive error rather than an HTTP-based exception.
                if (hasErrorDetailsExtension)
                {
                    await CheckErrorDetails(client, job);
                }
                // If not, then just throw the error we got
                throw;
            }
        }
        catch (HttpRequestException ex) when (ex.InnerException is TaskCanceledException or SocketException)
        {
            // A connection timeout manifests as TaskCanceledException
            _logger.LogError(ex, "Error connecting to ESCL device");
            throw new DeviceCommunicationException();
        }
        catch (TaskCanceledException)
        {
        }
    }

    public async Task ScanRaw(RawScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents,
        IRawScanSink sink)
    {
        if (cancelToken.IsCancellationRequested) return;

        // ESCL responses are already encoded by the scanner. Keep the request in the acquisition-only API, but use
        // the existing setting mapper to select a lossless device format where the device advertises one. The body
        // is copied directly to the sink below; it is never decoded into an IMemoryImage.
        var scanOptions = ToScanOptions(options);
        try
        {
            var (client, caps) = await GetEsclClientWithCaps(scanOptions, cancelToken, scanEvents);
            if (client == null || caps == null) return;
            var status = await client.GetStatus();
            bool hasProgressExtension = caps.Naps2Extensions?.Contains("Progress") ?? false;
            bool hasErrorDetailsExtension = caps.Naps2Extensions?.Contains("ErrorDetails") ?? false;
            bool hasShortTimeoutExtension = caps.Naps2Extensions?.Contains("ShortTimeout") ?? false;
            bool hasAnyDpiExtension = caps.Naps2Extensions?.Contains("AnyDpi") ?? false;
            var scanSettings = GetScanSettings(scanOptions, caps, hasAnyDpiExtension, preferRawContainer: true);
            Action<double>? progressCallback = hasProgressExtension ? scanEvents.PageProgress : null;

            if (cancelToken.IsCancellationRequested) return;

            // Raw acquisition has no software processing settings. An empty successful result is still reported so
            // callers can distinguish a configured session from a driver that never reached configuration.
            sink.ConfigurationApplied(new DriverProcessingResult());
            VerifyStatus(status, scanSettings);

            var (job, effectiveScanSettings) = await CreateScanJobAndCorrectInvalidSettings(client, scanSettings);

            var cancelOnce = new Once(() => client.CancelJob(job).AssertNoAwait());
            using var cancelReg = cancelToken.Register(cancelOnce.Run);

            try
            {
                if (effectiveScanSettings.InputSource == EsclInputSource.Platen)
                {
                    scanEvents.PageStart();
                }
                while (true)
                {
                    if (effectiveScanSettings.InputSource != EsclInputSource.Platen)
                    {
                        scanEvents.PageStart();
                    }

                    using var document = await GetNextDocumentStreamWithRetries(
                        client, job, progressCallback, hasShortTimeoutExtension, cancelToken);
                    if (document == null) break;
                    await WriteRawDocument(document, options, effectiveScanSettings, sink, cancelToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ESCL raw driver error");
                cancelOnce.Run();
                // The root cause for the exception might be a server-side scanning error, so prefer to throw a more
                // descriptive error rather than an HTTP-based exception.
                if (hasErrorDetailsExtension)
                {
                    await CheckErrorDetails(client, job);
                }
                throw;
            }
        }
        catch (HttpRequestException ex) when (ex.InnerException is TaskCanceledException or SocketException)
        {
            // A connection timeout manifests as TaskCanceledException
            _logger.LogError(ex, "Error connecting to ESCL device");
            throw new DeviceCommunicationException();
        }
        catch (TaskCanceledException) when (cancelToken.IsCancellationRequested)
        {
            // Cancellation is a normal end of an acquisition. A timeout from the HTTP client or the response body
            // is allowed to propagate to the caller for recovery and diagnostics.
        }
    }

    private static ScanOptions ToScanOptions(RawScanOptions options)
    {
        return new ScanOptions
        {
            Driver = options.Driver,
            Device = options.Device,
            PaperSource = options.PaperSource,
            Dpi = options.Dpi,
            PageSize = options.PageSize,
            BitDepth = options.BitDepth,
            PageAlign = options.PageAlign,
            UseNativeUI = options.UseNativeUI,
            DialogParent = options.DialogParent,
            EsclOptions = options.EsclOptions,
            // Prefer a lossless format for a raw handoff. If a device does not advertise PNG, the existing mapper
            // falls back to PDF, which can carry multiple logical pages in one response.
            MaxQuality = true,
            Quality = 100
        };
    }

    private async Task<RawDocumentStream?> GetNextDocumentStreamWithRetries(EsclClient client, EsclJob job,
        Action<double>? progress, bool shortTimeout, CancellationToken cancellationToken)
    {
        int retries = 0;
        while (true)
        {
            try
            {
                return await client.NextDocumentStream(job, progress, shortTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ESCL NextDocument raw error");
                if (++retries > MAX_DOCUMENT_TRIES)
                {
                    _logger.LogDebug("ESCL NextDocument raw failed, no more retries left");
                    throw;
                }
                EsclJobState jobState = EsclJobState.Unknown;
                try
                {
                    var status = await client.GetStatus();
                    jobState = status.JobStates.Get(job.UriPath);
                }
                catch (Exception)
                {
                    _logger.LogDebug("ESCL GetStatus failed, could not get job state");
                }
                if (jobState is not (EsclJobState.Pending or EsclJobState.Processing or EsclJobState.Unknown))
                {
                    // Only retry if the job is pending or processing.
                    _logger.LogDebug("ESCL NextDocument raw failed, not retrying as job state is {State}", jobState);
                    throw;
                }
                _logger.LogDebug("ESCL NextDocument raw failed, retrying as job state is {State}", jobState);
                await Task.Delay(DOCUMENT_RETRY_INTERVAL, cancellationToken);
            }
        }
    }

    private async Task WriteRawDocument(RawDocumentStream document, RawScanOptions options,
        EsclScanSettings scanSettings, IRawScanSink sink, CancellationToken cancellationToken)
    {
        var payload = GetRawPayloadInfo(document.ContentType, document.ContentLocation, scanSettings.ColorMode);
        var header = new RawScanArtifactHeader
        {
            Type = payload.Type,
            ImageFormat = payload.ImageFormat,
            ContentType = document.ContentType,
            FileExtension = payload.FileExtension,
            PixelFormat = payload.PixelFormat,
            SubPixelType = payload.SubPixelType,
            FrameType = RawScanFrameType.Image,
            HorizontalResolution = scanSettings.XResolution,
            VerticalResolution = scanSettings.YResolution,
            PageCount = payload.IsContainer ? null : 1,
            SourceId = options.Device?.ID
        };

        var writer = sink.BeginArtifact(header);
        try
        {
            long byteLength = 0;
            var buffer = new byte[64 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Bound each read so an interrupted scanner connection does not leave an acquisition blocked forever.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(60_000);
                int bytesRead;
                try
                {
                    bytesRead = await document.Data.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("The eSCL response stopped sending data before the document completed.");
                }
                if (bytesRead == 0) break;

                // RawScanArtifactWriter.Write has a synchronous borrowed-buffer contract. It must copy the span
                // before returning, allowing this buffer to be reused for the next network read.
                writer.Write(buffer.AsSpan(0, bytesRead), new RawBlockLayout
                {
                    Offset = byteLength,
                    FrameType = RawScanFrameType.Image,
                    PageIndex = payload.IsContainer ? null : 0,
                    FrameIndex = 0,
                    IsLastBlock = document.ContentLength is { } length && byteLength + bytesRead >= length
                });
                byteLength += bytesRead;
            }

            if (byteLength == 0)
            {
                throw new IOException("The eSCL response had no data, the connection may have been interrupted.");
            }

            await writer.CompleteAsync(new RawScanArtifactMetadata
            {
                ByteLength = byteLength,
                Width = null,
                Height = null,
                HorizontalResolution = scanSettings.XResolution,
                VerticalResolution = scanSettings.YResolution,
                PixelFormat = payload.PixelFormat,
                SubPixelType = payload.SubPixelType,
                FrameType = RawScanFrameType.Image,
                ImageFormat = payload.ImageFormat,
                ContentType = document.ContentType,
                PageCount = payload.IsContainer ? null : 1,
                FrameCount = payload.IsContainer ? null : 1,
                PageSide = RawScanPageSide.Unknown,
                IsDuplex = scanSettings.Duplex,
                DeviceId = options.Device?.ID,
                SourceId = options.Device?.ID,
                AdditionalMetadata = document.ContentLocation == null
                    ? null
                    : new Dictionary<string, string?> { ["content-location"] = document.ContentLocation }
            }, cancellationToken);
        }
        catch
        {
            try
            {
                await writer.AbortAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ESCL raw artifact abort failed");
            }
            throw;
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }

    private static RawPayloadInfo GetRawPayloadInfo(string? contentType, string? contentLocation,
        EsclColorMode colorMode)
    {
        var normalizedContentType = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        var locationExtension = GetExtension(contentLocation);
        var (type, imageFormat, extension, isContainer) = normalizedContentType switch
        {
            ContentTypes.PDF => (RawScanArtifactType.EncodedContainer, ImageFileFormat.Unknown, ".pdf", true),
            "application/x-pdf" => (RawScanArtifactType.EncodedContainer, ImageFileFormat.Unknown, ".pdf", true),
            "image/tiff" or "image/tif" or "application/tiff" =>
                (RawScanArtifactType.EncodedContainer, ImageFileFormat.Tiff, ".tiff", true),
            ContentTypes.PNG => (RawScanArtifactType.EncodedImage, ImageFileFormat.Png, ".png", false),
            ContentTypes.JPEG or "image/jpg" or "image/pjpeg" =>
                (RawScanArtifactType.EncodedImage, ImageFileFormat.Jpeg, ".jpg", false),
            _ => locationExtension switch
            {
                ".pdf" => (RawScanArtifactType.EncodedContainer, ImageFileFormat.Unknown, ".pdf", true),
                ".tif" or ".tiff" =>
                    (RawScanArtifactType.EncodedContainer, ImageFileFormat.Tiff, ".tiff", true),
                ".png" => (RawScanArtifactType.EncodedImage, ImageFileFormat.Png, ".png", false),
                ".jpg" or ".jpeg" => (RawScanArtifactType.EncodedImage, ImageFileFormat.Jpeg, ".jpg", false),
                _ => (RawScanArtifactType.Unknown, ImageFileFormat.Unknown, locationExtension, false)
            }
        };

        var (pixelFormat, subPixelType) = colorMode switch
        {
            EsclColorMode.BlackAndWhite1 => (ImagePixelFormat.BW1, SubPixelType.Bit),
            EsclColorMode.Grayscale8 or EsclColorMode.Grayscale16 => (ImagePixelFormat.Gray8, SubPixelType.Gray),
            _ => (ImagePixelFormat.RGB24, SubPixelType.Rgb)
        };
        return new RawPayloadInfo(type, imageFormat, extension, isContainer, pixelFormat, subPixelType);
    }

    private static string? GetExtension(string? contentLocation)
    {
        if (string.IsNullOrWhiteSpace(contentLocation)) return null;
        var path = contentLocation!.Split('?', 2)[0];
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? null : extension.ToLowerInvariant();
    }

    private sealed record RawPayloadInfo(RawScanArtifactType Type, ImageFileFormat ImageFormat,
        string? FileExtension, bool IsContainer, ImagePixelFormat PixelFormat, SubPixelType SubPixelType);

    private async Task<(EsclClient?, EsclCapabilities?)> GetEsclClientWithCaps(ScanOptions options,
        CancellationToken cancelToken, IScanEvents scanEvents)
    {
        EsclClient client;
        string deviceId = options.Device!.ID;

        void SetUpClient()
        {
            client.SecurityPolicy = options.EsclOptions.SecurityPolicy;
            client.Logger = _logger;
            client.CancelToken = cancelToken;
        }
        void MaybeSendNewUris()
        {
            string? iconUri = client.IconUri;
            string connectionUri = client.ConnectionUri;
            if (iconUri != options.Device.IconUri || connectionUri != options.Device.ConnectionUri)
            {
                scanEvents.DeviceUriChanged(iconUri, connectionUri);
            }
        }

        // If we only have a URI to connect, just use it.
        if (deviceId.StartsWith("http://") || deviceId.StartsWith("https://"))
        {
            client = new EsclClient(new Uri(deviceId));
            SetUpClient();
            EsclCapabilities caps;
            try
            {
                caps = await client.GetCapabilities();
            }
            catch (HttpRequestException)
            {
                throw new DeviceOfflineException();
            }
            return (client, caps);
        }

        // If we have both a UUID and a ConnectionUri, race an mDNS request with a GetCapabilities request.
        // This is the best of both worlds:
        // - If the connection info is up to date, we connect directly immediately.
        // - If the IP has changed, we find out and fall back to that
        // TODO: Maybe racing [GetCapabilities] with [mDNS + GetCapabilities] is slightly more optimal
        var serviceTask = FindDeviceEsclService(options, cancelToken);
        if (options.Device!.ConnectionUri != null)
        {
            client = new EsclClient(new Uri(options.Device.ConnectionUri));
            SetUpClient();
            var capsTask = client.GetCapabilities();
            await Task.WhenAny(capsTask, serviceTask);
            if (capsTask.Status == TaskStatus.RanToCompletion)
            {
                return (client, await capsTask);
            }
        }

        // If we have no known connection info (or failed to connect with it), use the mDNS response for the client
        var service = await serviceTask;
        if (cancelToken.IsCancellationRequested) return (null, null);
        if (service == null) throw new DeviceOfflineException();
        client = new EsclClient(service);
        MaybeSendNewUris();
        SetUpClient();
        return (client, await client.GetCapabilities());
    }

    private async Task<(EsclJob Job, EsclScanSettings EffectiveSettings)> CreateScanJobAndCorrectInvalidSettings(
        EsclClient client, EsclScanSettings scanSettings)
    {
        _logger.LogDebug("Creating ESCL job: format {Format}, source {Source}, mode {Mode}",
            scanSettings.DocumentFormat, scanSettings.InputSource, scanSettings.ColorMode);
        EsclScanSettings effectiveSettings = scanSettings;
        EsclJob job;
        try
        {
            job = await client.CreateScanJob(scanSettings);
        }
        catch (HttpRequestException ex) when (scanSettings.ColorMode == EsclColorMode.BlackAndWhite1 &&
                                              ex.Message.Contains("409 (Conflict)"))
        {
            effectiveSettings = scanSettings with
            {
                ColorMode = EsclColorMode.Grayscale8,
                DocumentFormat = ContentTypes.JPEG
            };
            _logger.LogDebug("Scanning in Grayscale instead of Black & White due to HTTP 409 response");
            job = await client.CreateScanJob(effectiveSettings);
        }
        return (job, effectiveSettings);
    }

    private async Task<RawDocument?> GetNextDocumentWithRetries(EsclClient client, EsclJob job,
        Action<double>? progress, bool shortTimeout)
    {
        int retries = 0;
        while (true)
        {
            try
            {
                return await client.NextDocument(job, progress, shortTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ESCL NextDocument error");
                if (++retries > MAX_DOCUMENT_TRIES)
                {
                    _logger.LogDebug("ESCL NextDocument failed, no more retries left");
                    throw;
                }
                EsclJobState jobState = EsclJobState.Unknown;
                try
                {
                    var status = await client.GetStatus();
                    jobState = status.JobStates.Get(job.UriPath);
                }
                catch (Exception)
                {
                    _logger.LogDebug("ESCL GetStatus failed, could not get job state");
                }
                if (jobState is not (EsclJobState.Pending or EsclJobState.Processing or EsclJobState.Unknown))
                {
                    // Only retry if the job is pending or processing
                    _logger.LogDebug("ESCL NextDocument failed, not retrying as job state is {State}", jobState);
                    throw;
                }
                _logger.LogDebug("ESCL NextDocument failed, retrying as job state is {State}", jobState);
                await Task.Delay(DOCUMENT_RETRY_INTERVAL);
            }
        }
    }

    private async Task CheckErrorDetails(EsclClient client, EsclJob job)
    {
        string? errorDetails = null;
        try
        {
            errorDetails = await client.ErrorDetails(job);
        }
        catch (Exception)
        {
            // Ignore
        }
        if (!string.IsNullOrEmpty(errorDetails))
        {
            RemotingHelper.HandleErrors(errorDetails!.FromXml<Error>());
        }
    }

    private static void VerifyStatus(EsclScannerStatus status, EsclScanSettings scanSettings)
    {
        if (status.State is EsclScannerState.Processing or EsclScannerState.Testing or EsclScannerState.Stopped)
        {
            throw new DeviceBusyException();
        }
        if (status.State == EsclScannerState.Down)
        {
            throw new DeviceOfflineException();
        }
        if (scanSettings.InputSource == EsclInputSource.Feeder)
        {
            if (status.AdfState == EsclAdfState.ScannerAdfEmpty)
            {
                throw new DeviceFeederEmptyException();
            }
            if (status.AdfState == EsclAdfState.ScannerAdfJam)
            {
                throw new DevicePaperJamException();
            }
            if (status.AdfState is not (EsclAdfState.Unknown or EsclAdfState.ScannerAdfProcessing
                or EsclAdfState.ScannedAdfLoaded))
            {
                throw new DeviceException(status.AdfState.ToString());
            }
        }
    }

    private async Task<EsclService?> FindDeviceEsclService(ScanOptions options, CancellationToken cancelToken)
    {
        var foundTcs = new TaskCompletionSource<EsclService?>();
        var deviceUuid = GetUuid(options.Device!);
        using var locator = new EsclServiceLocator(service =>
        {
            if (service.Uuid == deviceUuid)
            {
                foundTcs.TrySetResult(service);
            }
        });
        Task.Delay(options.EsclOptions.SearchTimeout, cancelToken)
            .ContinueWith(_ => foundTcs.TrySetResult(null)).AssertNoAwait();
        locator.Logger = _scanningContext.Logger;
        locator.Start();
        return await foundTcs.Task;
    }

    private IEnumerable<IMemoryImage> GetImagesFromRawDocument(ScanOptions options, RawDocument doc)
    {
        if (doc.ContentType == ContentTypes.PDF)
        {
            // TODO: For SDK some kind an error message if Pdfium isn't present
            var renderer = new PdfiumPdfRenderer();
            foreach (var image in renderer.Render(_scanningContext.ImageContext, doc.Data, doc.Data.Length,
                         PdfRenderSize.FromDpi(options.Dpi)))
            {
                yield return image;
            }
        }
        else
        {
            yield return _scanningContext.ImageContext.Load(doc.Data);
        }
    }

    private EsclScanSettings GetScanSettings(ScanOptions options, EsclCapabilities caps, bool hasAnyDpiExtension,
        bool preferRawContainer = false)
    {
        if (options.PaperSource == PaperSource.Feeder && caps.AdfSimplexCaps == null)
        {
            throw new NoFeederSupportException();
        }
        if (options.PaperSource == PaperSource.Duplex && caps.AdfDuplexCaps == null)
        {
            throw new NoDuplexSupportException();
        }
        if (options.PaperSource is PaperSource.Flatbed or PaperSource.Auto
            && caps.PlatenCaps == null && caps.AdfSimplexCaps != null)
        {
            options.PaperSource = PaperSource.Feeder;
        }

        var (inputCaps, inputSource, duplex) = options.PaperSource switch
        {
            PaperSource.Feeder => (caps.AdfSimplexCaps, EsclInputSource.Feeder, false),
            PaperSource.Duplex => (caps.AdfDuplexCaps, EsclInputSource.Feeder, true),
            _ => (caps.PlatenCaps, EsclInputSource.Platen, false),
        };
        inputCaps ??= new EsclInputCaps();

        var colorMode = options.BitDepth switch
        {
            BitDepth.Color => EsclColorMode.RGB24,
            BitDepth.Grayscale => EsclColorMode.Grayscale8,
            BitDepth.BlackAndWhite => EsclColorMode.BlackAndWhite1,
            _ => EsclColorMode.RGB24
        };
        int dpi = options.Dpi;

        var settingProfile = inputCaps.SettingProfiles.FirstOrDefault(x => x.ColorModes.Contains(colorMode))
                             ?? inputCaps.SettingProfiles.FirstOrDefault();

        if (settingProfile != null)
        {
            colorMode = MaybeCorrectColorMode(settingProfile, colorMode);

            if (!hasAnyDpiExtension)
            {
                var discreteResolutions =
                    settingProfile.DiscreteResolutions.Where(res => res.XResolution == res.YResolution)
                        .Select(res => res.XResolution).ToList();
                if (discreteResolutions.Any())
                {
                    dpi = discreteResolutions.OrderBy(v => Math.Abs(v - dpi)).First();
                }

                if (settingProfile.XResolutionRange != null && settingProfile.YResolutionRange != null)
                {
                    int min = Math.Max(settingProfile.XResolutionRange.Min, settingProfile.YResolutionRange.Min);
                    int max = Math.Min(settingProfile.XResolutionRange.Max, settingProfile.YResolutionRange.Max);
                    dpi = dpi.Clamp(min, max);
                }
            }

            _logger.LogDebug("ESCL setting profile supports formats: {Formats}",
                string.Join(",", settingProfile.DocumentFormats.Concat(settingProfile.DocumentFormatsExt).Distinct()));
        }

        var width = (int) Math.Round(options.PageSize!.WidthInInches * 300);
        var height = (int) Math.Round(options.PageSize!.HeightInInches * 300);
        if (inputCaps.MaxWidth is > 0)
        {
            width = Math.Min(width, inputCaps.MaxWidth.Value);
        }
        if (inputCaps.MaxHeight is > 0)
        {
            height = Math.Min(height, inputCaps.MaxHeight.Value);
        }

        var contentType = ContentTypes.JPEG;
        if (options.BitDepth == BitDepth.BlackAndWhite || options.MaxQuality)
        {
            bool supportsTiff = settingProfile != null && settingProfile.DocumentFormats
                .Concat(settingProfile.DocumentFormatsExt).Contains("image/tiff");
            bool supportsPng = settingProfile != null && settingProfile.DocumentFormats
                .Concat(settingProfile.DocumentFormatsExt).Contains(ContentTypes.PNG);
            contentType = preferRawContainer && supportsTiff
                ? "image/tiff"
                : supportsPng ? ContentTypes.PNG : ContentTypes.PDF;
        }

        return new EsclScanSettings
        {
            Width = width,
            Height = height,
            XResolution = dpi,
            YResolution = dpi,
            ColorMode = colorMode,
            InputSource = inputSource,
            Duplex = duplex,
            DocumentFormat = contentType,
            XOffset = options.PageAlign switch
            {
                HorizontalAlign.Left => inputCaps.MaxWidth is > 0 ? inputCaps.MaxWidth.Value - width : 0,
                HorizontalAlign.Center => inputCaps.MaxWidth is > 0 ? (inputCaps.MaxWidth.Value - width) / 2 : 0,
                _ => 0
            },
            CompressionFactor = caps.CompressionFactorSupport is { Min: 0, Max: 100, Step: 1 } ? options.Quality : null
            // TODO: Brightness/contrast, etc.
        };
    }

    private static EsclColorMode MaybeCorrectColorMode(EsclSettingProfile settingProfile, EsclColorMode colorMode)
    {
        if (settingProfile.ColorModes.Contains(colorMode))
        {
            return colorMode;
        }
        if (colorMode == EsclColorMode.BlackAndWhite1)
        {
            if (settingProfile.ColorModes.Contains(EsclColorMode.Grayscale8))
            {
                colorMode = EsclColorMode.Grayscale8;
            }
            else if (settingProfile.ColorModes.Contains(EsclColorMode.RGB24))
            {
                colorMode = EsclColorMode.RGB24;
            }
        }
        else if (colorMode == EsclColorMode.Grayscale8 && settingProfile.ColorModes.Contains(EsclColorMode.RGB24))
        {
            colorMode = EsclColorMode.RGB24;
        }
        return colorMode;
    }
}
