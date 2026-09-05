using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using Google.Protobuf;
using Grpc.Core;
using NAPS2.Scan;
using NAPS2.Serialization;

namespace NAPS2.Remoting.Worker;

/// <summary>
/// Version and feature negotiation for the worker raw acquisition stream.
/// </summary>
internal static class RawWorkerProtocol
{
    public const uint Version = 1;
    public const int MaxChunkBytes = 256 * 1024;
    public const long MaxBufferedBytes = 16L * 1024 * 1024;
    public const int MaxBufferedMessages = 4096;

    public const string OrderedArtifacts = "ordered-artifacts";
    public const string DriverProcessing = "driver-processing";

    public static readonly IReadOnlyList<string> Features =
    [
        OrderedArtifacts,
        DriverProcessing
    ];
}

/// <summary>
/// Indicates that a worker explicitly aborted a raw artifact. The worker
/// supplied reason is retained so callers can distinguish an aborted transfer
/// from a transport failure.
/// </summary>
internal sealed class RawWorkerArtifactAbortedException : Exception
{
    public RawWorkerArtifactAbortedException(ulong artifactId, string? reason)
        : base($"The worker aborted raw artifact {artifactId}: " +
               (string.IsNullOrEmpty(reason) ? "no reason was supplied" : reason))
    {
        ArtifactId = artifactId;
        Reason = reason;
    }

    public RawWorkerArtifactAbortedException(ulong artifactId, string? reason, Exception innerException)
        : base($"The worker aborted raw artifact {artifactId}: " +
               (string.IsNullOrEmpty(reason) ? "no reason was supplied" : reason), innerException)
    {
        ArtifactId = artifactId;
        Reason = reason;
    }

    public ulong ArtifactId { get; }

    public string? Reason { get; }
}

/// <summary>
/// Serializes the non-byte portion of the raw worker protocol. The XML
/// serializer is used for the common immutable records. Dictionaries and
/// object-valued processing settings are sent as explicit wire entries so an
/// interface-typed dictionary cannot be lost by the XML serializer.
/// </summary>
internal static class RawWorkerWireMapper
{
    public static RawScanProtocol ToProtocol(IEnumerable<string> requestedFeatures)
    {
        var requested = new HashSet<string>(requestedFeatures, StringComparer.Ordinal);
        var response = new RawScanProtocol { Version = RawWorkerProtocol.Version };
        if (requested.Count == 0)
        {
            response.AcceptedFeatures.Add(RawWorkerProtocol.Features);
        }
        else
        {
            response.AcceptedFeatures.Add(RawWorkerProtocol.Features.Where(requested.Contains));
        }
        return response;
    }

    public static RawScanArtifactStart ToArtifactStart(ulong artifactId, RawScanArtifactHeader header) =>
        new()
        {
            ArtifactId = artifactId,
            HeaderXml = header.ToXml()
        };

    public static RawScanArtifactHeader FromArtifactStart(RawScanArtifactStart start)
    {
        if (string.IsNullOrEmpty(start.HeaderXml))
        {
            throw new InvalidOperationException("The worker returned an empty raw artifact header.");
        }
        return start.HeaderXml.FromXml<RawScanArtifactHeader>();
    }

    public static RawScanArtifactChunk ToArtifactChunk(ulong artifactId, ulong sequence, ReadOnlySpan<byte> data,
        RawBlockLayout? layout)
    {
        var chunk = new RawScanArtifactChunk
        {
            ArtifactId = artifactId,
            Sequence = sequence,
            Data = ByteString.CopyFrom(data.ToArray())
        };
        if (layout != null)
        {
            chunk.LayoutXml = layout.ToXml();
        }
        return chunk;
    }

    public static RawBlockLayout? FromArtifactChunk(RawScanArtifactChunk chunk)
    {
        return string.IsNullOrEmpty(chunk.LayoutXml)
            ? null
            : chunk.LayoutXml.FromXml<RawBlockLayout>();
    }

    public static RawScanArtifactComplete ToArtifactComplete(ulong artifactId, RawScanArtifactMetadata metadata)
    {
        // AdditionalMetadata is interface typed. Keep it out of the XML and
        // carry it in a concrete repeated wire representation.
        var metadataXml = (metadata with { AdditionalMetadata = null }).ToXml();
        var response = new RawScanArtifactComplete
        {
            ArtifactId = artifactId,
            MetadataXml = metadataXml
        };
        if (metadata.AdditionalMetadata != null)
        {
            foreach (var item in metadata.AdditionalMetadata)
            {
                response.AdditionalMetadata.Add(new RawScanMetadataValue
                {
                    Name = item.Key,
                    HasValue = item.Value != null,
                    Value = item.Value ?? ""
                });
            }
        }
        return response;
    }

    public static RawScanArtifactMetadata FromArtifactComplete(RawScanArtifactComplete complete)
    {
        if (string.IsNullOrEmpty(complete.MetadataXml))
        {
            throw new InvalidOperationException("The worker returned empty raw artifact metadata.");
        }
        var metadata = complete.MetadataXml.FromXml<RawScanArtifactMetadata>();
        var additionalMetadata = complete.AdditionalMetadata.ToDictionary(
            x => x.Name,
            x => x.HasValue ? x.Value : null,
            StringComparer.Ordinal);
        return metadata with { AdditionalMetadata = additionalMetadata };
    }

    public static RawScanConfiguration ToConfiguration(DriverProcessingResult result)
    {
        var response = new RawScanConfiguration
        {
            FailureReason = result.FailureReason ?? ""
        };
        response.Settings.Add(result.Settings.Select(ToProcessingSetting));
        response.RequestedSettings.Add(result.RequestedSettings.Select(ToSettingValue));
        response.EffectiveSettings.Add(result.EffectiveSettings.Select(ToSettingValue));
        response.RejectedSettings.Add(result.RejectedSettings);
        response.UnsupportedSettings.Add(result.UnsupportedSettings);
        response.NeutralizedSettings.Add(result.NeutralizedSettings);
        response.FailedSettings.Add(result.FailedSettings);
        return response;
    }

    public static DriverProcessingResult FromConfiguration(RawScanConfiguration configuration)
    {
        var requested = configuration.RequestedSettings.ToDictionary(
            x => x.Name,
            x => FromValue(x.Value),
            StringComparer.Ordinal);
        var effective = configuration.EffectiveSettings.ToDictionary(
            x => x.Name,
            x => FromValue(x.Value),
            StringComparer.Ordinal);
        return new DriverProcessingResult
        {
            RequestedSettings = requested,
            EffectiveSettings = effective,
            Settings = configuration.Settings.Select(FromProcessingSetting).ToArray(),
            RejectedSettings = configuration.RejectedSettings.ToArray(),
            UnsupportedSettings = configuration.UnsupportedSettings.ToArray(),
            NeutralizedSettings = configuration.NeutralizedSettings.ToArray(),
            FailedSettings = configuration.FailedSettings.ToArray(),
            FailureReason = string.IsNullOrEmpty(configuration.FailureReason)
                ? null
                : configuration.FailureReason
        };
    }

    private static RawScanProcessingSetting ToProcessingSetting(DriverProcessingSetting setting) =>
        new()
        {
            Name = setting.Name,
            Status = (int) setting.Status,
            RequestedValue = ToValue(setting.RequestedValue),
            EffectiveValue = ToValue(setting.EffectiveValue),
            Message = setting.Message ?? ""
        };

    private static DriverProcessingSetting FromProcessingSetting(RawScanProcessingSetting setting) =>
        new()
        {
            Name = setting.Name,
            Status = Enum.IsDefined(typeof(DriverProcessingStatus), setting.Status)
                ? (DriverProcessingStatus) setting.Status
                : DriverProcessingStatus.Unknown,
            RequestedValue = FromValue(setting.RequestedValue),
            EffectiveValue = FromValue(setting.EffectiveValue),
            Message = string.IsNullOrEmpty(setting.Message) ? null : setting.Message
        };

    private static RawScanSettingValue ToSettingValue(KeyValuePair<string, object?> setting) =>
        new()
        {
            Name = setting.Key,
            Value = ToValue(setting.Value)
        };

    private static RawScanValue? ToValue(object? value)
    {
        if (value == null)
        {
            return new RawScanValue { IsNull = true };
        }

        string type;
        string text;
        switch (value)
        {
            case string stringValue:
                type = "string";
                text = stringValue;
                break;
            case bool boolValue:
                type = "bool";
                text = boolValue ? "true" : "false";
                break;
            case byte byteValue:
                type = "byte";
                text = byteValue.ToString(CultureInfo.InvariantCulture);
                break;
            case sbyte sbyteValue:
                type = "sbyte";
                text = sbyteValue.ToString(CultureInfo.InvariantCulture);
                break;
            case short shortValue:
                type = "short";
                text = shortValue.ToString(CultureInfo.InvariantCulture);
                break;
            case ushort ushortValue:
                type = "ushort";
                text = ushortValue.ToString(CultureInfo.InvariantCulture);
                break;
            case int intValue:
                type = "int";
                text = intValue.ToString(CultureInfo.InvariantCulture);
                break;
            case uint uintValue:
                type = "uint";
                text = uintValue.ToString(CultureInfo.InvariantCulture);
                break;
            case long longValue:
                type = "long";
                text = longValue.ToString(CultureInfo.InvariantCulture);
                break;
            case ulong ulongValue:
                type = "ulong";
                text = ulongValue.ToString(CultureInfo.InvariantCulture);
                break;
            case float floatValue:
                type = "float";
                text = floatValue.ToString(CultureInfo.InvariantCulture);
                break;
            case double doubleValue:
                type = "double";
                text = doubleValue.ToString(CultureInfo.InvariantCulture);
                break;
            case decimal decimalValue:
                type = "decimal";
                text = decimalValue.ToString(CultureInfo.InvariantCulture);
                break;
            case char charValue:
                type = "char";
                text = charValue.ToString();
                break;
            case Guid guidValue:
                type = "guid";
                text = guidValue.ToString();
                break;
            case DateTime dateTimeValue:
                type = "datetime";
                text = dateTimeValue.ToString("O", CultureInfo.InvariantCulture);
                break;
            default:
                // Driver options are diagnostic values at this boundary. A
                // stable invariant string keeps unknown backend types
                // forward compatible without attempting arbitrary type
                // activation in the worker process.
                type = "string";
                text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                break;
        }
        return new RawScanValue { Type = type, Text = text };
    }

    private static object? FromValue(RawScanValue? value)
    {
        if (value == null || value.IsNull)
        {
            return null;
        }
        try
        {
            return value.Type switch
            {
                "string" => value.Text,
                "bool" => bool.Parse(value.Text),
                "byte" => byte.Parse(value.Text, CultureInfo.InvariantCulture),
                "sbyte" => sbyte.Parse(value.Text, CultureInfo.InvariantCulture),
                "short" => short.Parse(value.Text, CultureInfo.InvariantCulture),
                "ushort" => ushort.Parse(value.Text, CultureInfo.InvariantCulture),
                "int" => int.Parse(value.Text, CultureInfo.InvariantCulture),
                "uint" => uint.Parse(value.Text, CultureInfo.InvariantCulture),
                "long" => long.Parse(value.Text, CultureInfo.InvariantCulture),
                "ulong" => ulong.Parse(value.Text, CultureInfo.InvariantCulture),
                "float" => float.Parse(value.Text, CultureInfo.InvariantCulture),
                "double" => double.Parse(value.Text, CultureInfo.InvariantCulture),
                "decimal" => decimal.Parse(value.Text, CultureInfo.InvariantCulture),
                "char" => char.Parse(value.Text),
                "guid" => Guid.Parse(value.Text),
                "datetime" => DateTime.Parse(value.Text, CultureInfo.InvariantCulture),
                _ => value.Text
            };
        }
        catch (FormatException)
        {
            return value.Text;
        }
        catch (OverflowException)
        {
            return value.Text;
        }
    }
}

/// <summary>
/// Serializes server stream responses on one dedicated writer and bounds
/// queued raw bytes. A driver callback blocks only when the byte budget is
/// exhausted; it never performs final image encoding.
/// </summary>
internal sealed class RawWorkerResponsePump : IAsyncDisposable
{
    private readonly IServerStreamWriter<RawScanResponse> _responseStream;
    private readonly CancellationToken _cancellationToken;
    private readonly object _gate = new();
    private readonly Queue<PendingResponse> _queue = new();
    private readonly long _maxBufferedBytes;
    private readonly int _maxBufferedMessages;
    private readonly Task _pumpTask;
    private long _bufferedBytes;
    private bool _accepting = true;
    private Exception? _failure;

    public RawWorkerResponsePump(IServerStreamWriter<RawScanResponse> responseStream,
        CancellationToken cancellationToken,
        long maxBufferedBytes = RawWorkerProtocol.MaxBufferedBytes,
        int maxBufferedMessages = RawWorkerProtocol.MaxBufferedMessages)
    {
        if (maxBufferedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
        if (maxBufferedMessages <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedMessages));
        _responseStream = responseStream;
        _cancellationToken = cancellationToken;
        _maxBufferedBytes = maxBufferedBytes;
        _maxBufferedMessages = maxBufferedMessages;
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Queues a message and returns a task that completes after the message
    /// has been accepted by the gRPC stream. The caller may ignore the task
    /// while the raw producer continues; artifact completion awaits it.
    /// </summary>
    public Task Enqueue(RawScanResponse response, int rawBytes = 0)
    {
        if (rawBytes < 0 || rawBytes > _maxBufferedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(rawBytes));
        }

        _cancellationToken.ThrowIfCancellationRequested();
        var delivery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            while (_accepting && _failure == null &&
                   (_bufferedBytes + rawBytes > _maxBufferedBytes || _queue.Count >= _maxBufferedMessages))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 100);
            }
            ThrowIfUnavailable();
            _queue.Enqueue(new PendingResponse(response, rawBytes, delivery));
            _bufferedBytes += rawBytes;
            Monitor.PulseAll(_gate);
        }
        return delivery.Task;
    }

    public async Task CompleteAsync()
    {
        lock (_gate)
        {
            _accepting = false;
            Monitor.PulseAll(_gate);
        }
        await _pumpTask.ConfigureAwait(false);
        ThrowIfFailed();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal must not mask the original scan/transport exception.
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            PendingResponse item;
            lock (_gate)
            {
                while (_queue.Count == 0 && _accepting && _failure == null)
                {
                    Monitor.Wait(_gate, 100);
                }
                if (_failure != null)
                {
                    return;
                }
                if (_queue.Count == 0 && !_accepting)
                {
                    return;
                }
                item = _queue.Dequeue();
            }

            try
            {
                await _responseStream.WriteAsync(item.Response).ConfigureAwait(false);
                item.Delivery.TrySetResult(true);
                lock (_gate)
                {
                    _bufferedBytes -= item.RawBytes;
                    Monitor.PulseAll(_gate);
                }
            }
            catch (Exception ex)
            {
                Fail(ex, item);
                return;
            }
        }
    }

    private void Fail(Exception failure, PendingResponse? inFlight = null)
    {
        lock (_gate)
        {
            var error = _failure ??= failure;
            inFlight?.Delivery.TrySetException(error);
            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                item.Delivery.TrySetException(error);
            }
            _bufferedBytes = 0;
            _accepting = false;
            Monitor.PulseAll(_gate);
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_failure != null)
        {
            ExceptionDispatchInfo.Capture(_failure).Throw();
        }
        if (!_accepting)
        {
            throw new InvalidOperationException("The raw worker response stream is already completed.");
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure != null)
        {
            ExceptionDispatchInfo.Capture(_failure).Throw();
        }
    }

    private sealed record PendingResponse(RawScanResponse Response, int RawBytes,
        TaskCompletionSource<bool> Delivery);
}

/// <summary>
/// Server-side sink that turns the driver's synchronous borrowed-buffer
/// callbacks into ordered raw worker responses.
/// </summary>
internal sealed class RawWorkerSink : IRawScanSink
{
    private readonly RawWorkerResponsePump _pump;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, RawWorkerArtifactWriter> _writers = new();
    private long _nextArtifactId;

    public RawWorkerSink(RawWorkerResponsePump pump)
    {
        _pump = pump;
    }

    public void ConfigurationApplied(DriverProcessingResult result)
    {
        _pump.Enqueue(new RawScanResponse
        {
            Configuration = RawWorkerWireMapper.ToConfiguration(result)
        });
    }

    public IRawScanArtifactWriter BeginArtifact(RawScanArtifactHeader header)
    {
        var artifactId = checked((ulong) Interlocked.Increment(ref _nextArtifactId));
        var writer = new RawWorkerArtifactWriter(this, artifactId, _pump);
        lock (_gate)
        {
            _writers.Add(artifactId, writer);
        }
        _pump.Enqueue(new RawScanResponse
        {
            ArtifactStart = RawWorkerWireMapper.ToArtifactStart(artifactId, header)
        });
        return writer;
    }

    public async Task AbortOpenArtifactsAsync(string reason)
    {
        RawWorkerArtifactWriter[] writers;
        lock (_gate)
        {
            writers = _writers.Values.ToArray();
        }
        foreach (var writer in writers)
        {
            try
            {
                await writer.AbortAsync(reason).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The stream may have failed while a writer was draining. A
                // later writer still needs the chance to release its state.
            }
        }
    }

    internal void Remove(ulong artifactId)
    {
        lock (_gate)
        {
            _writers.Remove(artifactId);
        }
    }
}

/// <summary>
/// Server-side writer for one artifact. It splits large driver buffers into
/// bounded wire chunks while retaining a delivery task for completion.
/// </summary>
internal sealed class RawWorkerArtifactWriter : IRawScanArtifactWriter
{
    private readonly RawWorkerSink _owner;
    private readonly ulong _artifactId;
    private readonly RawWorkerResponsePump _pump;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Task _lastDelivery = Task.CompletedTask;
    private ulong _sequence;
    private WriterState _state;

    public RawWorkerArtifactWriter(RawWorkerSink owner, ulong artifactId, RawWorkerResponsePump pump)
    {
        _owner = owner;
        _artifactId = artifactId;
        _pump = pump;
    }

    public void Write(ReadOnlySpan<byte> data, RawBlockLayout? layout = null)
    {
        // Write has no cancellation token because it is called from a driver
        // callback whose borrowed buffer is only valid until this method
        // returns. Serialize the complete copy/enqueue operation against the
        // terminal operations so no chunk can follow a complete/abort event.
        _operationGate.Wait();
        try
        {
            EnsureOpen();
            if (data.Length == 0)
            {
                EnqueueChunk(ReadOnlySpan<byte>.Empty, layout);
                return;
            }

            var offset = 0;
            var firstChunk = true;
            while (offset < data.Length)
            {
                var length = Math.Min(RawWorkerProtocol.MaxChunkBytes, data.Length - offset);
                EnqueueChunk(data.Slice(offset, length), firstChunk ? layout : null);
                offset += length;
                firstChunk = false;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task CompleteAsync(RawScanArtifactMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            _state = WriterState.Completing;

            await WaitForDeliveryAsync(_lastDelivery, cancellationToken).ConfigureAwait(false);
            var delivery = _pump.Enqueue(new RawScanResponse
            {
                ArtifactComplete = RawWorkerWireMapper.ToArtifactComplete(_artifactId, metadata)
            });
            await WaitForDeliveryAsync(delivery, cancellationToken).ConfigureAwait(false);
            _state = WriterState.Completed;
            _owner.Remove(_artifactId);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default) =>
        await AbortAsync("The raw artifact was aborted.", cancellationToken).ConfigureAwait(false);

    public async Task AbortAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == WriterState.Completed || _state == WriterState.Aborted)
            {
                return;
            }
            if (_state != WriterState.Open && _state != WriterState.Completing && _state != WriterState.Aborting)
            {
                throw new InvalidOperationException("The raw artifact writer is no longer open.");
            }
            _state = WriterState.Aborting;

            await WaitForDeliveryAsync(_lastDelivery, cancellationToken).ConfigureAwait(false);
            var delivery = _pump.Enqueue(new RawScanResponse
            {
                ArtifactAbort = new RawScanArtifactAbort
                {
                    ArtifactId = _artifactId,
                    Reason = reason ?? ""
                }
            });
            await WaitForDeliveryAsync(delivery, cancellationToken).ConfigureAwait(false);
            _state = WriterState.Aborted;
            _owner.Remove(_artifactId);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        AbortAsync().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync() => new(AbortAsync());

    private void EnsureOpen()
    {
        if (_state != WriterState.Open)
        {
            throw new InvalidOperationException("The raw artifact writer is no longer open.");
        }
    }

    private void EnqueueChunk(ReadOnlySpan<byte> data, RawBlockLayout? layout)
    {
        var chunk = RawWorkerWireMapper.ToArtifactChunk(_artifactId, _sequence++, data, layout);
        _lastDelivery = _pump.Enqueue(new RawScanResponse { ArtifactChunk = chunk }, data.Length);
    }

    private static async Task WaitForDeliveryAsync(Task delivery, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await delivery.ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(delivery, Task.Delay(Timeout.Infinite, cancellationToken))
            .ConfigureAwait(false);
        if (completed != delivery)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        await delivery.ConfigureAwait(false);
    }

    private enum WriterState
    {
        Open,
        Completing,
        Completed,
        Aborting,
        Aborted
    }
}
