using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure.Settings;
using Farm.Testing.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Regression coverage for issue #2118: <c>MoonrakerClient.SpoolmanProxyRequestAsync</c>
/// previously bounded every proxied Spoolman call - reads and writes alike - with
/// <see cref="BackendTimeoutSettings.PrintControlTimeout"/> (60s by default). A printer that is
/// powered down but still holds its network address black-holes the TCP connection instead of
/// refusing it, so a read-only status probe (e.g. the spool list used by filament coverage and
/// attention runout) ran the full print-control budget and stalled the whole fleet projection.
/// Read-only (GET) proxy calls must use the short status-poll budget; state-mutating calls keep
/// the longer print-control budget since Spoolman may be persisting/validating data.
/// </summary>
public class MoonrakerSpoolmanProxyTimeoutTests
{
    private static (MoonrakerClient Client, HttpClient Http, TaskCompletionSource<CancellationToken> RequestStarted) CreateHangingClient(
        BackendTimeoutSettings timeouts, TimeProvider clock)
    {
        TaskCompletionSource<CancellationToken> requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken ct) =>
            {
                // Simulate a printer that is powered off but still holds its network
                // address: the connection black-holes rather than refusing, so the only
                // way the call ever returns is via the per-request timeout cancelling ct.
                requestStarted.TrySetResult(ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable: Task.Delay should have thrown on cancellation");
            });

        HttpClient http = new(handler.Object);
        MoonrakerClient client = new(http, NullLogger<MoonrakerClient>.Instance, timeouts, timeProvider: clock);
        return (client, http, requestStarted);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("get")]
    public async Task SpoolmanProxyRequestAsync_GetOnBlackHoledPrinter_BoundedByStatusPollTimeoutNotPrintControlTimeout(string method)
    {
        BackendTimeoutSettings timeouts = new()
        {
            StatusPollTimeoutSeconds = 1,
            PrintControlTimeoutSeconds = 15,
        };
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var harness = CreateHangingClient(timeouts, clock);
        using HttpClient http = harness.Http;

        Task<string?> request = harness.Client.SpoolmanProxyRequestAsync(
            "http://printer-offline.local:7125", method, "/api/v1/spool");

        await AssertTimeoutAsync(request, harness.RequestStarted, clock, timeouts.StatusPollTimeout);
    }

    [Fact]
    public async Task GetSpoolmanSpoolsAsync_OnBlackHoledPrinter_BoundedByStatusPollTimeout()
    {
        // GetSpoolmanSpoolsAsync is the exact call path used by
        // FilamentCoverageSpoolResolver.ResolveNativeAsync for fleet coverage and the
        // Attention runout source that composes it (#2118).
        BackendTimeoutSettings timeouts = new()
        {
            StatusPollTimeoutSeconds = 1,
            PrintControlTimeoutSeconds = 15,
        };
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var harness = CreateHangingClient(timeouts, clock);
        using HttpClient http = harness.Http;

        Task<string?> request = harness.Client.GetSpoolmanSpoolsAsync("http://printer-offline.local:7125");
        await AssertTimeoutAsync(request, harness.RequestStarted, clock, timeouts.StatusPollTimeout);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task SpoolmanProxyRequestAsync_MutatingVerbOnBlackHoledPrinter_StillUsesPrintControlTimeout(string method)
    {
        // Spoolman mutations (create/update/delete spool, filament, vendor) proxy through the
        // same method and legitimately need the longer budget - Spoolman may be
        // persisting/validating data, unlike a plain status read. This must remain unbounded by
        // the short status-poll timeout so a slow-but-genuine write is not falsely reported as
        // unavailable.
        BackendTimeoutSettings timeouts = new()
        {
            StatusPollTimeoutSeconds = 1,
            PrintControlTimeoutSeconds = 4,
        };
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var harness = CreateHangingClient(timeouts, clock);
        using HttpClient http = harness.Http;

        Task<string?> request = harness.Client.SpoolmanProxyRequestAsync(
            "http://printer-offline.local:7125", method, "/api/v1/spool/1", body: new { remaining_weight = 100 });

        await AssertTimeoutAsync(request, harness.RequestStarted, clock, timeouts.PrintControlTimeout);
    }

    [Fact]
    public async Task SpoolmanProxyRequestAsync_CallerCancelsBeforeDeadline_PreservesLinkedCancellation()
    {
        BackendTimeoutSettings timeouts = new() { PrintControlTimeoutSeconds = 4 };
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var harness = CreateHangingClient(timeouts, clock);
        using HttpClient http = harness.Http;
        using CancellationTokenSource caller = new();

        Task<string?> request = harness.Client.SpoolmanProxyRequestAsync(
            "http://printer-offline.local:7125", "PATCH", "/api/v1/spool/1", ct: caller.Token);
        CancellationToken requestToken = await harness.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        requestToken.IsCancellationRequested.Should().BeFalse();
        caller.Cancel();

        requestToken.IsCancellationRequested.Should().BeTrue();
        (await request.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeNull();
        clock.ActiveTimerCount.Should().Be(0);
    }

    private static async Task AssertTimeoutAsync(
        Task<string?> request, TaskCompletionSource<CancellationToken> requestStarted, ManualTimeProvider clock, TimeSpan expectedTimeout)
    {
        CancellationToken requestToken = await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        requestToken.CanBeCanceled.Should().BeTrue();
        clock.ActiveTimerCount.Should().Be(1);

        // Inspect cancellation itself, not a continuation's scheduler-dependent elapsed time.
        clock.Advance(expectedTimeout - TimeSpan.FromTicks(1));
        requestToken.IsCancellationRequested.Should().BeFalse("the selected timeout must not expire early");
        request.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromTicks(1));
        requestToken.IsCancellationRequested.Should().BeTrue("the selected timeout must expire at its exact boundary");

        (await request.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeNull("a black-holed request is unavailable");
        clock.ActiveTimerCount.Should().Be(0, "the request must dispose its timeout timer");
    }
}
