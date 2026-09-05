using System.Threading;
using NAPS2.Scan;
using NAPS2.Scan.Internal;
using NSubstitute;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class RawScanControllerTests : ContextualTests
{
    [Fact]
    public async Task ScanRawForwardsToDriverOnlyBridgePath()
    {
        var bridge = Substitute.For<IScanBridge>();
        bridge.ScanRaw(Arg.Any<RawScanOptions>(), Arg.Any<CancellationToken>(), Arg.Any<IScanEvents>(),
                Arg.Any<IRawScanSink>())
            .Returns(Task.CompletedTask);
        var bridgeFactory = Substitute.For<IScanBridgeFactory>();
        bridgeFactory.Create(Arg.Any<Driver>()).Returns(bridge);
        var postProcessor = Substitute.For<ILocalPostProcessor>();
        var sink = Substitute.For<IRawScanSink>();
        var controller = new ScanController(ScanningContext, postProcessor, new ScanOptionsValidator(), bridgeFactory);
        var options = new RawScanOptions
        {
            Driver = Driver.Escl,
            Device = new ScanDevice(Driver.Escl, "device", "Device")
        };

        await controller.ScanRawAsync(options, sink);

        bridgeFactory.Received(1).Create(Driver.Escl);
        await bridge.Received(1).ScanRaw(
            Arg.Is<RawScanOptions>(x => x.Device == options.Device && x.Dpi == 100 && x.PageSize == PageSize.Letter),
            Arg.Any<CancellationToken>(), Arg.Any<IScanEvents>(), sink);
        postProcessor.DidNotReceiveWithAnyArgs().PostProcess(default!, default!, default!);
    }

    [Fact]
    public async Task ScanRawRequiresDevice()
    {
        var bridgeFactory = Substitute.For<IScanBridgeFactory>();
        var controller = new ScanController(ScanningContext, Substitute.For<ILocalPostProcessor>(),
            new ScanOptionsValidator(), bridgeFactory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.ScanRawAsync(new RawScanOptions { Driver = Driver.Escl }, Substitute.For<IRawScanSink>()));

        bridgeFactory.DidNotReceiveWithAnyArgs().Create(default(Driver));
    }
}
