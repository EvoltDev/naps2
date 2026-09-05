using System.IO;
using System.Net.Http;
using System.Threading;

namespace NAPS2.Escl.Client;

/// <summary>
/// Describes an eSCL document whose response body is still available as a stream.
/// </summary>
/// <remarks>
/// The response is owned by this instance. Consumers must dispose the instance after the response body has been
/// copied. Keeping the response open here lets callers consume large documents without first buffering them in
/// memory.
/// </remarks>
public sealed class RawDocumentStream : IDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly RawDocumentProgress? _progress;
    private bool _disposed;

    internal RawDocumentStream(HttpResponseMessage response, Stream data,
        RawDocumentProgress? progress)
    {
        _response = response;
        Data = data;
        _progress = progress;
        ContentType = response.Content.Headers.ContentType?.MediaType;
        ContentLocation = response.Content.Headers.ContentLocation?.ToString();
        ContentLength = response.Content.Headers.ContentLength;
    }

    /// <summary>
    /// Gets the response body. The stream is owned by this object and is disposed with it.
    /// </summary>
    public Stream Data { get; }

    public string? ContentType { get; }

    public string? ContentLocation { get; }

    public long? ContentLength { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _progress?.Dispose();
        }
        finally
        {
            try
            {
                Data.Dispose();
            }
            finally
            {
                _response.Dispose();
            }
        }
    }
}

/// <summary>
/// Owns the eSCL progress response for the lifetime of the corresponding document response.
/// </summary>
/// <remarks>
/// Disposing the response stream is necessary in addition to cancelling the token: some older HTTP handlers do not
/// observe cancellation while a chunked response is waiting for its next line. The cancellation source is retained
/// until the reader task exits so that it is never disposed concurrently with an in-flight read.
/// </remarks>
internal sealed class RawDocumentProgress : IDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cancellation;
    private int _disposed;
    private int _readerAttached;
    private int _readerCompleted;
    private int _cancellationDisposed;

    internal RawDocumentProgress(HttpResponseMessage response, Stream stream)
    {
        _response = response;
        _stream = stream;
        _cancellation = new CancellationTokenSource();
    }

    internal Stream Stream => _stream;

    internal CancellationToken CancellationToken => _cancellation.Token;

    internal bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    internal void AttachReader()
    {
        Interlocked.Exchange(ref _readerAttached, 1);
        TryDisposeCancellation();
    }

    internal void ReaderCompleted()
    {
        Interlocked.Exchange(ref _readerCompleted, 1);
        TryDisposeCancellation();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The reader can finish at the same time as document disposal. Its completion path owns final CTS
                // disposal, so cancellation is already complete in this case.
            }

            // Closing the stream and response also unblocks handlers that do not honor CancellationToken on reads.
            try
            {
                _stream.Dispose();
            }
            finally
            {
                _response.Dispose();
            }
        }

        TryDisposeCancellation();
    }

    private void TryDisposeCancellation()
    {
        if (Volatile.Read(ref _disposed) == 0 ||
            (Volatile.Read(ref _readerAttached) != 0 && Volatile.Read(ref _readerCompleted) == 0))
        {
            return;
        }

        if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
        {
            _cancellation.Dispose();
        }
    }
}
