using System.Threading;
using Microsoft.Extensions.Logging;
using NAPS2.Remoting.Worker;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// Proxy implementation of ITwainController that interacts with a Twain session in a worker process.
/// </summary>
internal class RemoteTwainController : ITwainController
{
    private readonly ScanningContext _scanningContext;

    public RemoteTwainController(ScanningContext scanningContext)
    {
        _scanningContext = scanningContext;
    }

    public async Task<List<ScanDevice>> GetDeviceList(ScanOptions options)
    {
        using var workerContext = CreateWorker(options);
        return await workerContext.Service.TwainGetDeviceList(options);
    }

    public async Task<ScanCaps> GetCaps(ScanOptions options)
    {
        using var workerContext = CreateWorker(options);
        return await workerContext.Service.TwainGetCaps(options);
    }

    public async Task StartScan(ScanOptions options, ITwainEvents twainEvents, CancellationToken cancelToken)
    {
        using var workerContext = CreateWorker(options);
        await workerContext.Service.TwainScan(options, cancelToken, twainEvents);
        if (cancelToken.IsCancellationRequested)
        {
            // We need to report cancellation so that TwainImageProcessor doesn't return a partial image
            _scanningContext.Logger.LogDebug("NAPS2.TW - Sending cancel event");
            twainEvents.TransferCanceled(new TwainTransferCanceled());
            // We also want to wait until the worker closes so we guarantee the parent process doesn't die before the
            // TWAIN process has a chance to clean up
            await workerContext.Stop();
        }
    }

    public async Task StartRawScan(RawScanOptions options, IScanEvents scanEvents, IRawScanSink sink,
        CancellationToken cancelToken)
    {
        using var workerContext = CreateWorker(options.TwainOptions.Dsm);
        await workerContext.Service.ScanRaw(options, cancelToken, scanEvents, sink);
        if (cancelToken.IsCancellationRequested)
        {
            _scanningContext.Logger.LogDebug("NAPS2.TW - Stopping raw scan worker after cancellation");
            await workerContext.Stop();
        }
    }

    private WorkerContext CreateWorker(ScanOptions options)
    {
        return CreateWorker(options.TwainOptions.Dsm);
    }

    private WorkerContext CreateWorker(TwainDsm dsm)
    {
        if (_scanningContext.WorkerFactory == null)
        {
            // Shouldn't hit this case
            throw new InvalidOperationException();
        }
        return _scanningContext.CreateWorker(
            dsm == TwainDsm.NewX64 || !PlatformCompat.System.SupportsWinX86Worker
                ? WorkerType.Native
                : WorkerType.WinX86)!;
    }
}
