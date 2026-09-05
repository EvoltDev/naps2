using System.Threading;
using Grpc.Core;
using Google.Protobuf.Collections;
using NAPS2.ImportExport.Email;
using NAPS2.ImportExport.Email.Mapi;
using NAPS2.Scan;
using NAPS2.Scan.Exceptions;
using NAPS2.Scan.Internal;
using NAPS2.Scan.Internal.Twain;
using NAPS2.Scan.Internal.Wia;
using NAPS2.Serialization;

namespace NAPS2.Remoting.Worker;

internal class WorkerServiceAdapter
{
    private readonly WorkerService.WorkerServiceClient _client;

    public WorkerServiceAdapter(CallInvoker callInvoker)
    {
        _client = new WorkerService.WorkerServiceClient(callInvoker);
    }

    public void Init(string? recoveryFolderPath)
    {
        var req = new InitRequest { RecoveryFolderPath = recoveryFolderPath ?? "" };
        var resp = _client.Init(req);
        RemotingHelper.HandleErrors(resp.Error);
    }

    public WiaConfiguration? Wia10NativeUI(string scanDevice, IntPtr hwnd)
    {
        var req = new Wia10NativeUiRequest
        {
            DeviceId = scanDevice,
            Hwnd = (ulong) hwnd
        };
        var resp = _client.Wia10NativeUi(req);
        RemotingHelper.HandleErrors(resp.Error);
        if (string.IsNullOrEmpty(resp.WiaConfigurationXml))
        {
            return null;
        }
        return resp.WiaConfigurationXml.FromXml<WiaConfiguration>();
    }

    public async Task GetDevices(ScanOptions options, CancellationToken cancelToken, Action<ScanDevice> callback)
    {
        var req = new GetDevicesRequest
        {
            OptionsXml = options.ToXml()
        };
        try
        {
            var streamingCall = _client.GetDevices(req, cancellationToken: cancelToken);
            while (await streamingCall.ResponseStream.MoveNext())
            {
                var resp = streamingCall.ResponseStream.Current;
                RemotingHelper.HandleErrors(resp.Error);
                callback(resp.DeviceXml.FromXml<ScanDevice>());
            }
        }
        catch (RpcException ex)
        {
            if (ex.StatusCode == StatusCode.Unavailable)
            {
                throw new ScanDriverUnknownException(PlatformCompat.System.WorkerCrashMessage, ex);
            }
            if (ex.Status.StatusCode != StatusCode.Cancelled)
            {
                throw;
            }
        }
    }

    public async Task<ScanCaps> GetCaps(ScanOptions options, CancellationToken cancelToken)
    {
        try
        {
            var req = new GetCapsRequest { OptionsXml = options.ToXml() };
            var resp = await _client.GetCapsAsync(req, cancellationToken: cancelToken);
            RemotingHelper.HandleErrors(resp.Error);
            return resp.ScanCapsXml.FromXml<ScanCaps>();
        }
        catch (RpcException ex)
        {
            if (ex.StatusCode == StatusCode.Unavailable)
            {
                throw new ScanDriverUnknownException(PlatformCompat.System.WorkerCrashMessage, ex);
            }
            throw;
        }
    }

    public async Task Scan(ScanningContext scanningContext, ScanOptions options, CancellationToken cancelToken,
        IScanEvents scanEvents, Action<ProcessedImage, string> imageCallback)
    {
        var req = new ScanRequest
        {
            OptionsXml = options.ToXml()
        };
        try
        {
            var streamingCall = _client.Scan(req, cancellationToken: cancelToken);
            while (await streamingCall.ResponseStream.MoveNext())
            {
                var resp = streamingCall.ResponseStream.Current;
                RemotingHelper.HandleErrors(resp.Error);
                if (resp.PageStart != null)
                {
                    scanEvents.PageStart();
                }
                if (resp.Progress != null)
                {
                    scanEvents.PageProgress(resp.Progress.Value);
                }
                if (resp.Image != null)
                {
                    var renderableImage = ImageSerializer.Deserialize(scanningContext, resp.Image,
                        new DeserializeImageOptions());
                    imageCallback?.Invoke(renderableImage, resp.Image.RenderedFilePath);
                }
                if (resp.DeviceUriChanged != null)
                {
                    scanEvents.DeviceUriChanged(resp.DeviceUriChanged.IconUri, resp.DeviceUriChanged.ConnectionUri);
                }
            }
        }
        catch (RpcException ex)
        {
            if (ex.StatusCode == StatusCode.Unavailable)
            {
                throw new ScanDriverUnknownException(PlatformCompat.System.WorkerCrashMessage, ex);
            }
            if (ex.StatusCode != StatusCode.Cancelled)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Acquires raw driver data from a worker. The stream contains no
    /// ProcessedImage or ImageSerializer payloads; artifact chunks are
    /// forwarded directly to the caller's raw sink.
    /// </summary>
    public async Task ScanRaw(RawScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents,
        IRawScanSink sink)
    {
        var request = new RawScanRequest
        {
            OptionsXml = options.ToXml(),
            ProtocolVersion = RawWorkerProtocol.Version
        };
        request.Features.Add(RawWorkerProtocol.Features);

        AsyncServerStreamingCall<RawScanResponse>? streamingCall = null;
        var writers = new Dictionary<ulong, IRawScanArtifactWriter>();
        var nextSequences = new Dictionary<ulong, ulong>();
        var allWriters = new List<IRawScanArtifactWriter>();
        try
        {
            streamingCall = _client.ScanRaw(request, cancellationToken: cancelToken);
            var protocolReceived = false;
            while (await streamingCall.ResponseStream.MoveNext(cancelToken).ConfigureAwait(false))
            {
                var response = streamingCall.ResponseStream.Current;
                RemotingHelper.HandleErrors(response.Error);
                switch (response.PayloadCase)
                {
                    case RawScanResponse.PayloadOneofCase.Protocol:
                        ValidateProtocol(response.Protocol, request.Features, ref protocolReceived);
                        break;
                    case RawScanResponse.PayloadOneofCase.Configuration:
                        sink.ConfigurationApplied(RawWorkerWireMapper.FromConfiguration(response.Configuration));
                        break;
                    case RawScanResponse.PayloadOneofCase.ArtifactStart:
                    {
                        var artifact = response.ArtifactStart;
                        if (artifact.ArtifactId == 0 || writers.ContainsKey(artifact.ArtifactId))
                        {
                            throw new InvalidOperationException("The worker returned a duplicate raw artifact id.");
                        }
                        var writer = sink.BeginArtifact(RawWorkerWireMapper.FromArtifactStart(artifact)) ??
                                     throw new InvalidOperationException(
                                         $"The raw sink returned no writer for artifact {artifact.ArtifactId}.");
                        allWriters.Add(writer);
                        writers.Add(artifact.ArtifactId, writer);
                        nextSequences.Add(artifact.ArtifactId, 0);
                        break;
                    }
                    case RawScanResponse.PayloadOneofCase.ArtifactChunk:
                    {
                        var chunk = response.ArtifactChunk;
                        if (!writers.TryGetValue(chunk.ArtifactId, out var writer))
                        {
                            throw new InvalidOperationException(
                                $"The worker returned a raw chunk for unknown artifact {chunk.ArtifactId}.");
                        }
                        if (chunk.Sequence != nextSequences[chunk.ArtifactId])
                        {
                            throw new InvalidOperationException(
                                $"The worker returned raw chunk {chunk.Sequence} out of order for artifact " +
                                $"{chunk.ArtifactId}; expected {nextSequences[chunk.ArtifactId]}.");
                        }
                        if (chunk.Data.Length > RawWorkerProtocol.MaxChunkBytes)
                        {
                            throw new InvalidOperationException(
                                $"The worker returned an oversized raw chunk ({chunk.Data.Length} bytes).");
                        }
                        nextSequences[chunk.ArtifactId]++;
                        // ByteString is immutable, but the raw sink contract
                        // requires the writer to own its copy before return.
                        // Passing a temporary array preserves that boundary.
                        var data = chunk.Data.ToByteArray();
                        writer.Write(data, RawWorkerWireMapper.FromArtifactChunk(chunk));
                        break;
                    }
                    case RawScanResponse.PayloadOneofCase.ArtifactComplete:
                    {
                        var complete = response.ArtifactComplete;
                        if (!writers.TryGetValue(complete.ArtifactId, out var writer))
                        {
                            throw new InvalidOperationException(
                                $"The worker completed unknown raw artifact {complete.ArtifactId}.");
                        }
                        await writer.CompleteAsync(RawWorkerWireMapper.FromArtifactComplete(complete), cancelToken)
                            .ConfigureAwait(false);
                        // Keep the writer registered until its terminal
                        // operation has succeeded. The finally block still
                        // disposes it after it is removed from the open set.
                        writers.Remove(complete.ArtifactId);
                        nextSequences.Remove(complete.ArtifactId);
                        break;
                    }
                    case RawScanResponse.PayloadOneofCase.ArtifactAbort:
                    {
                        var abort = response.ArtifactAbort;
                        if (!writers.TryGetValue(abort.ArtifactId, out var writer))
                        {
                            throw new InvalidOperationException(
                                $"The worker aborted unknown raw artifact {abort.ArtifactId}.");
                        }
                        try
                        {
                            await writer.AbortAsync(cancelToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            throw new RawWorkerArtifactAbortedException(abort.ArtifactId, abort.Reason, ex);
                        }
                        // An abort is a terminal protocol failure even when
                        // the local sink accepted the cleanup operation. The
                        // server supplied reason is part of the exception.
                        writers.Remove(abort.ArtifactId);
                        nextSequences.Remove(abort.ArtifactId);
                        throw new RawWorkerArtifactAbortedException(abort.ArtifactId, abort.Reason);
                    }
                    case RawScanResponse.PayloadOneofCase.PageStart:
                        scanEvents.PageStart();
                        break;
                    case RawScanResponse.PayloadOneofCase.Progress:
                        scanEvents.PageProgress(response.Progress.Value);
                        break;
                    case RawScanResponse.PayloadOneofCase.DeviceUriChanged:
                        scanEvents.DeviceUriChanged(response.DeviceUriChanged.IconUri,
                            response.DeviceUriChanged.ConnectionUri);
                        break;
                    case RawScanResponse.PayloadOneofCase.None:
                        throw new InvalidOperationException("The worker returned an empty raw scan response.");
                    case RawScanResponse.PayloadOneofCase.Error:
                        // HandleErrors above throws for this case. Keep the
                        // switch exhaustive even when a malformed or empty
                        // error payload does not contain a remoting type.
                        throw new InvalidOperationException(
                            string.IsNullOrEmpty(response.Error.Message)
                                ? "The worker returned an unspecified raw scan error."
                                : response.Error.Message);
                    default:
                        throw new InvalidOperationException("The worker returned an unknown raw scan response.");
                }
            }

            if (!protocolReceived)
            {
                throw new InvalidOperationException("The worker did not negotiate the raw scan protocol.");
            }
            if (writers.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The worker closed the raw scan with {writers.Count} open artifact(s): " +
                    string.Join(", ", writers.Keys));
            }
        }
        catch (RpcException ex)
        {
            if (ex.StatusCode == StatusCode.Unavailable)
            {
                throw new ScanDriverUnknownException(PlatformCompat.System.WorkerCrashMessage, ex);
            }
            if (ex.StatusCode != StatusCode.Cancelled)
            {
                throw;
            }
        }
        finally
        {
            try
            {
                streamingCall?.Dispose();
            }
            finally
            {
                foreach (var writer in allWriters)
                {
                    try
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Preserve the stream/driver error. Cleanup is best
                        // effort after a terminal transport failure.
                    }
                }
            }
        }
    }

    private static void ValidateProtocol(RawScanProtocol protocol, RepeatedField<string> requestedFeatures,
        ref bool protocolReceived)
    {
        if (protocolReceived)
        {
            throw new InvalidOperationException("The worker negotiated the raw scan protocol more than once.");
        }
        if (protocol.Version != RawWorkerProtocol.Version)
        {
            throw new InvalidOperationException(
                $"Unsupported raw worker protocol version {protocol.Version}; expected {RawWorkerProtocol.Version}.");
        }
        var acceptedFeatures = protocol.AcceptedFeatures.ToHashSet(StringComparer.Ordinal);
        foreach (var requiredFeature in RawWorkerProtocol.Features)
        {
            if (requestedFeatures.Contains(requiredFeature) && !acceptedFeatures.Contains(requiredFeature))
            {
                throw new InvalidOperationException(
                    $"The worker does not support required raw scan feature '{requiredFeature}'.");
            }
        }
        protocolReceived = true;
    }

    public async Task<bool> CanLoadMapi(string? clientName)
    {
        var req = new LoadMapiRequest { ClientName = clientName };
        var resp = await _client.LoadMapiAsync(req);
        return resp.Loaded;
    }

    public async Task<MapiSendMailReturnCode> SendMapiEmail(string? clientName, EmailMessage message)
    {
        var req = new SendMapiEmailRequest { ClientName = clientName, EmailMessageXml = message.ToXml() };
        var resp = await _client.SendMapiEmailAsync(req);
        RemotingHelper.HandleErrors(resp.Error);
        return resp.ReturnCodeXml.FromXml<MapiSendMailReturnCode>();
    }

    public byte[] RenderThumbnail(ImageContext imageContext, ProcessedImage image, int size)
    {
        var req = new RenderThumbnailRequest
        {
            Image = ImageSerializer.Serialize(image, new SerializeImageOptions
            {
                RequireFileStorage = true
            }),
            Size = size
        };
        var resp = _client.RenderThumbnail(req);
        RemotingHelper.HandleErrors(resp.Error);
        return resp.Thumbnail.ToByteArray();
    }

    public byte[] RenderPdf(string path, float dpi)
    {
        var req = new RenderPdfRequest
        {
            Path = path,
            Dpi = dpi
        };
        var resp = _client.RenderPdf(req);
        RemotingHelper.HandleErrors(resp.Error);
        return resp.Image.ToByteArray();
    }

    public void StopWorker()
    {
        _client.StopWorker(new StopWorkerRequest());
    }

    public async Task TwainScan(ScanOptions options, CancellationToken cancelToken, ITwainEvents twainEvents)
    {
        var req = new TwainScanRequest
        {
            OptionsXml = options.ToXml()
        };
        try
        {
            var streamingCall = _client.TwainScan(req, cancellationToken: cancelToken);
            while (await streamingCall.ResponseStream.MoveNext())
            {
                var resp = streamingCall.ResponseStream.Current;
                RemotingHelper.HandleErrors(resp.Error);
                if (resp.PageStart != null)
                {
                    twainEvents.PageStart(resp.PageStart);
                }
                if (resp.NativeImage != null)
                {
                    twainEvents.NativeImageTransferred(resp.NativeImage);
                }
                if (resp.MemoryBuffer != null)
                {
                    twainEvents.MemoryBufferTransferred(resp.MemoryBuffer);
                }
                if (resp.TransferCanceled != null)
                {
                    twainEvents.TransferCanceled(resp.TransferCanceled);
                }
            }
        }
        catch (RpcException ex)
        {
            if (ex.StatusCode == StatusCode.Unavailable)
            {
                throw new ScanDriverUnknownException(PlatformCompat.System.WorkerCrashMessage, ex);
            }
            if (ex.StatusCode != StatusCode.Cancelled)
            {
                throw;
            }
        }
    }

    public async Task<List<ScanDevice>> TwainGetDeviceList(ScanOptions options)
    {
        try
        {
            var req = new GetDeviceListRequest { OptionsXml = options.ToXml() };
            var resp = await _client.TwainGetDeviceListAsync(req);
            RemotingHelper.HandleErrors(resp.Error);
            return resp.DeviceListXml.FromXml<List<ScanDevice>>();
        }
        catch (RpcException ex)
        {
            if (ex.StatusCode == StatusCode.Unavailable)
            {
                throw new ScanDriverUnknownException(PlatformCompat.System.WorkerCrashMessage, ex);
            }
            throw;
        }
    }

    public async Task<ScanCaps> TwainGetCaps(ScanOptions options)
    {
        try
        {
            var req = new GetCapsRequest { OptionsXml = options.ToXml() };
            var resp = await _client.TwainGetCapsAsync(req);
            RemotingHelper.HandleErrors(resp.Error);
            return resp.ScanCapsXml.FromXml<ScanCaps>();
        }
        catch (RpcException ex)
        {
            if (ex.StatusCode == StatusCode.Unavailable)
            {
                throw new ScanDriverUnknownException(PlatformCompat.System.WorkerCrashMessage, ex);
            }
            throw;
        }
    }

    public ProcessedImage ImportPostProcess(ScanningContext scanningContext, ProcessedImage img, int? thumbnailSize,
        BarcodeDetectionOptions barcodeDetectionOptions)
    {
        var req = new ImportPostProcessRequest
        {
            Image = ImageSerializer.Serialize(img, new SerializeImageOptions
            {
                RequireFileStorage = true,
                TransferOwnership = true
            }),
            ThumbnailSize = thumbnailSize ?? 0,
            BarcodeDetectionOptionsXml = barcodeDetectionOptions.ToXml()
        };
        var resp = _client.ImportPostProcess(req);
        RemotingHelper.HandleErrors(resp.Error);
        return ImageSerializer.Deserialize(scanningContext, resp.Image, new DeserializeImageOptions());
    }
}
