using System.Net;
using System.Net.Http;
using System.Threading;
using NAPS2.Escl.Client;
using NAPS2.Escl.Server;
using NSubstitute;
using Xunit;

namespace NAPS2.Escl.Tests;

public class RawDocumentStreamTests
{
    [Fact]
    public void DisposeClosesDocumentAndProgressResponses()
    {
        var documentBody = new TrackingStream();
        var documentResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(documentBody)
        };
        var progressBody = new TrackingStream();
        var progressResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(progressBody)
        };
        var progress = new RawDocumentProgress(progressResponse, progressBody);
        var document = new RawDocumentStream(documentResponse, documentBody, progress);

        document.Dispose();

        Assert.True(documentBody.IsDisposed);
        Assert.True(progressBody.IsDisposed);
    }

    [Fact(Timeout = 60_000)]
    public async Task NextDocumentStreamExposesResponseBodyWithoutBufferingIt()
    {
        var payload = new byte[256 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        var job = Substitute.For<IEsclScanJob>();
        job.ContentType.Returns("image/tiff");
        job.WaitForNextDocument(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        job.WriteDocumentTo(Arg.Any<Stream>()).Returns(callInfo =>
        {
            var stream = callInfo.Arg<Stream>();
            for (var offset = 0; offset < payload.Length; offset += 4096)
            {
                var length = Math.Min(4096, payload.Length - offset);
                stream.Write(payload, offset, length);
            }
            return Task.CompletedTask;
        });

        using var server = new EsclServer
        {
            SecurityPolicy = EsclSecurityPolicy.ServerDisableHttps
        };
        var uuid = Guid.NewGuid().ToString("D");
        var deviceConfig = new EsclDeviceConfig
        {
            Capabilities = new EsclCapabilities
            {
                Version = "2.0",
                MakeAndModel = "Test Scanner",
                Uuid = uuid
            },
            CreateJob = _ => job
        };
        server.AddDevice(deviceConfig);
        await server.Start();

        try
        {
            var client = new EsclClient(new EsclService
            {
                IpV4 = IPAddress.Loopback,
                IpV6 = IPAddress.IPv6Loopback,
                Host = $"[{IPAddress.IPv6Loopback}]",
                RemoteEndpoint = IPAddress.IPv6Loopback,
                Port = deviceConfig.Port,
                TlsPort = deviceConfig.TlsPort,
                RootUrl = "eSCL",
                Tls = false,
                Uuid = uuid
            });
            var scanJob = await client.CreateScanJob(new EsclScanSettings
            {
                Width = 2550,
                Height = 3300,
                XResolution = 300,
                YResolution = 300,
                ColorMode = EsclColorMode.RGB24,
                InputSource = EsclInputSource.Platen,
                DocumentFormat = "image/tiff"
            });

            using var document = await client.NextDocumentStream(scanJob);
            Assert.NotNull(document);
            Assert.Equal("image/tiff", document!.ContentType);

            using var output = new MemoryStream();
            await document.Data.CopyToAsync(output);

            Assert.Equal(payload, output.ToArray());
        }
        finally
        {
            await server.Stop();
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
