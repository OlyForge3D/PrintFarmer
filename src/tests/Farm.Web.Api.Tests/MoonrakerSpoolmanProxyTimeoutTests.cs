using System.Diagnostics;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure.Settings;
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
    private static (MoonrakerClient client, Mock<HttpMessageHandler> handler) CreateHangingClient(
        BackendTimeoutSettings timeouts)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken ct) =>
            {
                // Simulate a printer that is powered off but still holds its network
                // address: the connection black-holes rather than refusing, so the only
                // way the call ever returns is via the per-request timeout cancelling ct.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable: Task.Delay should have thrown on cancellation");
            });

#pragma warning disable CA2000
        HttpClient http = new(handler.Object);
#pragma warning restore CA2000

        MoonrakerClient client = new(http, NullLogger<MoonrakerClient>.Instance, timeouts);
        return (client, handler);
    }

    [Fact]
    public async Task SpoolmanProxyRequestAsync_GetOnBlackHoledPrinter_BoundedByStatusPollTimeoutNotPrintControlTimeout()
    {
        BackendTimeoutSettings timeouts = new()
        {
            StatusPollTimeoutSeconds = 1,
            PrintControlTimeoutSeconds = 15,
        };
        (MoonrakerClient client, _) = CreateHangingClient(timeouts);

        Stopwatch stopwatch = Stopwatch.StartNew();
        string? result = await client.SpoolmanProxyRequestAsync(
            "http://printer-offline.local:7125", "GET", "/api/v1/spool");
        stopwatch.Stop();

        result.Should().BeNull("the black-holed request never completes and must be reported as unavailable");
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(timeouts.PrintControlTimeoutSeconds),
            "a read-only status probe must never wait for the print-control budget");
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(timeouts.StatusPollTimeoutSeconds + 5),
            "the GET must be bounded by the short status-poll timeout, not silently unbounded");
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
        (MoonrakerClient client, _) = CreateHangingClient(timeouts);

        Stopwatch stopwatch = Stopwatch.StartNew();
        string? result = await client.GetSpoolmanSpoolsAsync("http://printer-offline.local:7125");
        stopwatch.Stop();

        result.Should().BeNull();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(timeouts.PrintControlTimeoutSeconds));
    }

    [Fact]
    public async Task SpoolmanProxyRequestAsync_MutatingVerbOnBlackHoledPrinter_StillUsesPrintControlTimeout()
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
        (MoonrakerClient client, _) = CreateHangingClient(timeouts);

        Stopwatch stopwatch = Stopwatch.StartNew();
        string? result = await client.SpoolmanProxyRequestAsync(
            "http://printer-offline.local:7125", "PATCH", "/api/v1/spool/1", body: new { remaining_weight = 100 });
        stopwatch.Stop();

        result.Should().BeNull();

        // Assert against the *print-control* budget, not just >= the status-poll timeout: if the
        // fix regressed and mutations were incorrectly cut off by the short StatusPollTimeout
        // (1s), elapsed would still be >= 1s and a weaker ">= StatusPollTimeout" assertion would
        // pass anyway, hiding the bug. The 3s gap between the two timeouts (1s vs 4s) with a 1s
        // tolerance window means the assertion can only pass if the *longer* budget - not the
        // short one - actually gated this call, proving the longer timeout, not the short one,
        // was used.
        stopwatch.Elapsed.Should().BeCloseTo(
            TimeSpan.FromSeconds(timeouts.PrintControlTimeoutSeconds),
            TimeSpan.FromSeconds(1),
            "a mutating request must run for the full print-control budget, not the shorter status-poll timeout");
    }
}
