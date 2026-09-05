using System.Threading;
using GrpcDotNetNamedPipes;
using NAPS2.ImportExport.Email.Mapi;
using NAPS2.ImportExport.Images;
using NAPS2.Remoting.Worker;
using NAPS2.Scan;
using NAPS2.Scan.Exceptions;
using NAPS2.Scan.Internal;
using NAPS2.Scan.Internal.Twain;
using NSubstitute;
using Xunit;

namespace NAPS2.Sdk.Tests.Worker;

public class WorkerChannelTests : ContextualTests
{
    private Channel Start(IRemoteScanController remoteScanController = null, ThumbnailRenderer thumbnailRenderer = null,
        IMapiWrapper mapiWrapper = null, ITwainController twainController = null)
    {
        string pipeName = $"WorkerNamedPipeTests.{Path.GetRandomFileName()}";
        NamedPipeServer server = new NamedPipeServer(pipeName);
        WorkerService.BindService(server.ServiceBinder,
            new WorkerServiceImpl(ScanningContext, remoteScanController, thumbnailRenderer, mapiWrapper,
                twainController));
        server.Start();
        var client = new WorkerServiceAdapter(new NamedPipeChannel(".", pipeName));
        return new Channel
        {
            Server = server,
            Client = client
        };
    }

    [Fact]
    public void Init()
    {
        using var channel = Start();
        channel.Client.Init(@"C:\Somewhere");
        Assert.StartsWith(@"C:\Somewhere", ScanningContext.FileStorageManager.NextFilePath());
    }

    [Fact]
    public void Wia10NativeUi()
    {
        // TODO: This is not testable yet
        // channel.Client.Wia10NativeUI(...);
    }

    [Fact]
    public async Task GetDevices()
    {
        var remoteScanController = Substitute.For<IRemoteScanController>();
        using var channel = Start(remoteScanController);
        remoteScanController.GetDevices(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>(),
                Arg.Any<Action<ScanDevice>>())
            .Returns(x =>
            {
                var callback = (Action<ScanDevice>) x[2];
                callback(new ScanDevice(Driver.Wia, "test_id", "test_name"));
                return Task.CompletedTask;
            });

        var deviceList = new List<ScanDevice>();
        await channel.Client.GetDevices(new ScanOptions(), CancellationToken.None, deviceList.Add);

        Assert.Single(deviceList);
        Assert.Equal("test_id", deviceList[0].ID);
        Assert.Equal("test_name", deviceList[0].Name);
        _ = remoteScanController.Received().GetDevices(Arg.Any<ScanOptions>(), Arg.Any<CancellationToken>(),
            Arg.Any<Action<ScanDevice>>());
        remoteScanController.ReceivedCallsCount(1);
    }

    [Fact]
    public async Task ScanWithMemoryStorage()
    {
        await ScanInternalTest();
    }

    [Fact]
    public async Task ScanWithFileStorage()
    {
        SetUpFileStorage();
        await ScanInternalTest();
    }

    private async Task ScanInternalTest()
    {
        var remoteScanController = new MockRemoteScanController
        {
            Images =
            [
                CreateScannedImage(),
                CreateScannedImage()
            ]
        };

        using var channel = Start(remoteScanController);
        var receivedImages = new List<ProcessedImage>();
        await channel.Client.Scan(
            ScanningContext,
            new ScanOptions(),
            CancellationToken.None,
            ScanEvents.Stub,
            (img, path) => { receivedImages.Add(img); });

        Assert.Equal(2, receivedImages.Count);
        // TODO: Verify that thumbnails are set correctly (with and without revertible transforms)
    }

    [Fact]
    public async Task ScanException()
    {
        var remoteScanController = new MockRemoteScanController
        {
            Images =
            [
                CreateScannedImage(),
                CreateScannedImage()
            ],
            Exception = new DeviceException("Test error")
        };
        using var channel = Start(remoteScanController);
        var ex = await Assert.ThrowsAsync<DeviceException>(async () => await channel.Client.Scan(
            ScanningContext,
            new ScanOptions(),
            CancellationToken.None,
            ScanEvents.Stub,
            (img, path) => { }));
        Assert.Contains(nameof(MockRemoteScanController), ex.StackTrace);
        Assert.Contains("Test error", ex.Message);
    }

    [Fact]
    public async Task TwainScan()
    {
        var twainEvents = Substitute.For<ITwainEvents>();
        var twainController = Substitute.For<ITwainController>();

        twainController.StartScan(Arg.Any<ScanOptions>(), Arg.Any<TwainEvents>(), Arg.Any<CancellationToken>())
            .Returns(
                x =>
                {
                    var serverTwainEvents = (ITwainEvents) x[1];
                    serverTwainEvents.PageStart(new TwainPageStart());
                    serverTwainEvents.MemoryBufferTransferred(new TwainMemoryBuffer());
                    serverTwainEvents.PageStart(new TwainPageStart());
                    serverTwainEvents.NativeImageTransferred(new TwainNativeImage());
                    return Task.CompletedTask;
                });

        using var channel = Start(twainController: twainController);
        await channel.Client.TwainScan(new ScanOptions(), CancellationToken.None, twainEvents);

        twainEvents.Received().PageStart(Arg.Any<TwainPageStart>());
        twainEvents.Received().MemoryBufferTransferred(Arg.Any<TwainMemoryBuffer>());
        twainEvents.Received().PageStart(Arg.Any<TwainPageStart>());
        twainEvents.Received().NativeImageTransferred(Arg.Any<TwainNativeImage>());
        twainEvents.ReceivedCallsCount(4);
    }

    [Fact]
    public async Task ScanRawStreamsOrderedArtifactAndDriverEvents()
    {
        var remoteScanController = Substitute.For<IRemoteScanController>();
        remoteScanController.ScanRaw(Arg.Any<RawScanOptions>(), Arg.Any<CancellationToken>(),
                Arg.Any<IScanEvents>(), Arg.Any<IRawScanSink>())
            .Returns(callInfo =>
            {
                var scanEvents = (IScanEvents) callInfo[2];
                var sink = (IRawScanSink) callInfo[3];
                scanEvents.PageStart();
                scanEvents.PageProgress(.5);
                sink.ConfigurationApplied(new DriverProcessingResult
                {
                    Settings =
                    [
                        new DriverProcessingSetting
                        {
                            Name = "deskew",
                            Status = DriverProcessingStatus.Applied,
                            RequestedValue = true,
                            EffectiveValue = true
                        }
                    ],
                    RequestedSettings = new Dictionary<string, object?> { ["deskew"] = true },
                    EffectiveSettings = new Dictionary<string, object?> { ["deskew"] = true }
                });
                var artifact = sink.BeginArtifact(new RawScanArtifactHeader
                {
                    Type = RawScanArtifactType.NativeBitmap,
                    Width = 2,
                    Height = 2,
                    SourceId = "test-source"
                });
                artifact.Write(Array.Empty<byte>());
                artifact.Write(new byte[] { 1, 2 }, new RawBlockLayout
                {
                    Width = 2,
                    Height = 1,
                    Stride = 2,
                    FrameType = RawScanFrameType.Gray,
                    PageIndex = 0
                });
                artifact.Write(new byte[] { 3, 4 });
                return artifact.CompleteAsync(new RawScanArtifactMetadata
                {
                    ByteLength = 4,
                    Width = 2,
                    Height = 2,
                    FrameType = RawScanFrameType.Gray,
                    PageSide = RawScanPageSide.Front,
                    AdditionalMetadata = new Dictionary<string, string?>
                    {
                        ["driver"] = "worker",
                        ["optional"] = null
                    }
                });
            });

        using var channel = Start(remoteScanController);
        var scanEvents = Substitute.For<IScanEvents>();
        var sink = new CapturingRawSink();
        await channel.Client.ScanRaw(new RawScanOptions { Driver = Driver.Escl }, CancellationToken.None,
            scanEvents, sink);

        scanEvents.Received(1).PageStart();
        scanEvents.Received(1).PageProgress(.5);
        Assert.Single(sink.Configurations);
        Assert.Equal(true, sink.Configurations[0].EffectiveSettings["deskew"]);
        Assert.Single(sink.Artifacts);
        var capturedArtifact = sink.Artifacts[0];
        Assert.Equal(RawScanArtifactType.NativeBitmap, capturedArtifact.Header.Type);
        Assert.Equal("test-source", capturedArtifact.Header.SourceId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, capturedArtifact.Bytes.ToArray());
        Assert.Equal(RawScanFrameType.Gray, capturedArtifact.FirstLayout!.FrameType);
        Assert.Equal("worker", capturedArtifact.Metadata!.AdditionalMetadata!["driver"]);
        Assert.Null(capturedArtifact.Metadata!.AdditionalMetadata!["optional"]);
        Assert.Equal(3, capturedArtifact.WriteCount);
        Assert.Equal(1, capturedArtifact.DisposeCount);
    }

    [Fact]
    public async Task ScanRawFailsWithAbortReasonAndDisposesOpenArtifact()
    {
        var remoteScanController = Substitute.For<IRemoteScanController>();
        remoteScanController.ScanRaw(Arg.Any<RawScanOptions>(), Arg.Any<CancellationToken>(),
                Arg.Any<IScanEvents>(), Arg.Any<IRawScanSink>())
            .Returns(callInfo =>
            {
                var sink = (IRawScanSink) callInfo[3];
                sink.BeginArtifact(new RawScanArtifactHeader
                {
                    Type = RawScanArtifactType.EncodedContainer,
                    SourceId = "incomplete"
                });
                return Task.CompletedTask;
            });

        using var channel = Start(remoteScanController);
        var sink = new CapturingRawSink();
        var ex = await Assert.ThrowsAsync<RawWorkerArtifactAbortedException>(() => channel.Client.ScanRaw(
            new RawScanOptions { Driver = Driver.Escl }, CancellationToken.None,
            Substitute.For<IScanEvents>(), sink));

        Assert.Contains("driver ended before completing", ex.Reason!);
        Assert.Single(sink.Artifacts);
        Assert.Equal(1, sink.Artifacts[0].DisposeCount);
    }

    private class MockRemoteScanController : IRemoteScanController
    {
        public List<ProcessedImage> Images { get; set; } = [];

        public Exception Exception { get; set; }

        public Task GetDevices(ScanOptions options, CancellationToken cancelToken, Action<ScanDevice> callback) =>
            throw new NotSupportedException();

        public Task<ScanCaps> GetCaps(ScanOptions options, CancellationToken cancelToken)
        {
            return null!;
        }

        public Task Scan(ScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents,
            Action<ProcessedImage, PostProcessingContext> callback)
        {
            return Task.Run(() =>
            {
                foreach (var img in Images)
                {
                    callback(img, new PostProcessingContext());
                }

                if (Exception != null)
                {
                    throw Exception;
                }
            });
        }

        public Task ScanRaw(RawScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents,
            IRawScanSink sink) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingRawSink : IRawScanSink
    {
        public List<DriverProcessingResult> Configurations { get; } = [];

        public List<CapturedRawArtifact> Artifacts { get; } = [];

        public void ConfigurationApplied(DriverProcessingResult result)
        {
            Configurations.Add(result);
        }

        public IRawScanArtifactWriter BeginArtifact(RawScanArtifactHeader header)
        {
            var artifact = new CapturedRawArtifact(header);
            Artifacts.Add(artifact);
            return artifact;
        }
    }

    private sealed class CapturedRawArtifact : IRawScanArtifactWriter
    {
        private readonly List<byte> _bytes = [];

        public CapturedRawArtifact(RawScanArtifactHeader header)
        {
            Header = header;
        }

        public RawScanArtifactHeader Header { get; }

        public RawScanArtifactMetadata? Metadata { get; private set; }

        public RawBlockLayout? FirstLayout { get; private set; }

        public IReadOnlyList<byte> Bytes => _bytes;

        public int DisposeCount { get; private set; }

        public int WriteCount { get; private set; }

        public void Write(ReadOnlySpan<byte> data, RawBlockLayout? layout = null)
        {
            WriteCount++;
            FirstLayout ??= layout;
            _bytes.AddRange(data.ToArray());
        }

        public Task CompleteAsync(RawScanArtifactMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            Metadata = metadata;
            return Task.CompletedTask;
        }

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
            DisposeCount++;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return default;
        }
    }

    private class Channel : IDisposable
    {
        public NamedPipeServer Server { get; set; }

        public WorkerServiceAdapter Client { get; set; }

        public void Dispose()
        {
            Server.Kill();
        }
    }
}
