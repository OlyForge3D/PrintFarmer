using System.Net;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests;

public class MoonrakerSnapmakerU1CameraTests
{
    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenMonitorStarts_ReturnsJpegAndStopsAfterIdle()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xd9];
        RecordingJsonRpcClient rpc = new();
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(25));
        MoonrakerClient client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(jpeg)
        }, manager);

        byte[]? result = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");
        await rpc.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        result.Should().Equal(jpeg);
        rpc.Methods.Should().Equal("camera.start_monitor", "camera.stop_monitor");
    }

    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenCalledRapidly_RateLimitsStartMonitor()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xd9];
        RecordingJsonRpcClient rpc = new();
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        int httpFetches = 0;
        MoonrakerClient client = CreateClient(_ =>
        {
            httpFetches++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(jpeg)
            };
        }, manager);

        byte[]? first = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");
        byte[]? second = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");

        first.Should().Equal(jpeg);
        second.Should().Equal(jpeg);
        httpFetches.Should().Be(2);
        rpc.Methods.Count(m => m == "camera.start_monitor").Should().Be(1);
        rpc.Methods.Should().NotContain("camera.stop_monitor");
    }

    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenCalledConcurrently_CoalescesStartMonitor()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xd9];
        RecordingJsonRpcClient rpc = new() { StartDelay = TimeSpan.FromMilliseconds(50) };
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        int httpFetches = 0;
        MoonrakerClient client = CreateClient(_ =>
        {
            Interlocked.Increment(ref httpFetches);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(jpeg)
            };
        }, manager);

        byte[]?[] results = await Task.WhenAll(
            client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local"),
            client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local"));

        results.Should().OnlyContain(result => result.SequenceEqual(jpeg));
        httpFetches.Should().Be(2);
        rpc.Count("camera.start_monitor").Should().Be(1);
    }

    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenWebSocketStartFails_ReturnsNullWithoutHttpFetch()
    {
        RecordingJsonRpcClient rpc = new() { FailStart = true };
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        int httpFetches = 0;
        MoonrakerClient client = CreateClient(_ =>
        {
            httpFetches++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, manager);

        byte[]? result = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");

        result.Should().BeNull();
        httpFetches.Should().Be(0);
        rpc.Methods.Should().Equal("camera.start_monitor");
    }

    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenStartWasSentThenReplyFails_SchedulesCleanupStop()
    {
        RecordingJsonRpcClient rpc = new() { FailStartAfterSend = true };
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(25));
        int httpFetches = 0;
        MoonrakerClient client = CreateClient(_ =>
        {
            httpFetches++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, manager);

        byte[]? result = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");
        await rpc.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        result.Should().BeNull();
        httpFetches.Should().Be(0);
        rpc.Methods.Should().Equal("camera.start_monitor", "camera.stop_monitor");
    }

    [Fact]
    public async Task EnsureMonitorStartedAsync_WhenStopFails_RetriesBeforeClearingState()
    {
        RecordingJsonRpcClient rpc = new() { StopFailuresBeforeSuccess = 1 };
        SnapmakerU1CameraMonitorManager manager = new(
            rpc,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            maxStopRetries: 2);

        bool started = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);
        await rpc.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        started.Should().BeTrue();
        rpc.Methods.Should().Equal("camera.start_monitor", "camera.stop_monitor", "camera.stop_monitor");
    }

    private static MoonrakerClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ISnapmakerU1CameraMonitorManager manager)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                req.RequestUri?.AbsolutePath.Should().Be("/server/files/camera/monitor.jpg");
                return responder(req);
            });

#pragma warning disable CA2000
        HttpClient http = new(handler.Object);
#pragma warning restore CA2000
        return new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings(), manager);
    }

    private sealed class RecordingJsonRpcClient : IMoonrakerJsonRpcClient
    {
        public List<string> Methods { get; } = [];

        public TaskCompletionSource StopObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailStart { get; set; }

        public bool FailStartAfterSend { get; set; }

        public int StopFailuresBeforeSuccess { get; set; }

        public TimeSpan StartDelay { get; set; }

        public int Count(string method)
        {
            lock (Methods)
            {
                return Methods.Count(m => m == method);
            }
        }

        public Task SendMethodAsync(Uri baseUrl, string method, Farm.Infrastructure.Domain.PrinterCredential? credential, CancellationToken ct)
        {
            lock (Methods)
            {
                Methods.Add(method);
            }

            if (method == "camera.start_monitor" && FailStart)
            {
                throw new InvalidOperationException("connect timeout");
            }

            if (method == "camera.start_monitor" && StartDelay > TimeSpan.Zero)
            {
                return SendStartAfterDelayAsync(ct);
            }

            if (method == "camera.start_monitor" && FailStartAfterSend)
            {
                throw new MoonrakerJsonRpcException("reply failed", requestSent: true);
            }

            if (method == "camera.stop_monitor")
            {
                if (StopFailuresBeforeSuccess > 0)
                {
                    StopFailuresBeforeSuccess--;
                    throw new MoonrakerJsonRpcException("stop failed", requestSent: true);
                }

                StopObserved.TrySetResult();
            }

            return Task.CompletedTask;
        }

        private async Task SendStartAfterDelayAsync(CancellationToken ct)
        {
            await Task.Delay(StartDelay, ct);
            if (FailStartAfterSend)
            {
                throw new MoonrakerJsonRpcException("reply failed", requestSent: true);
            }
        }
    }
}
