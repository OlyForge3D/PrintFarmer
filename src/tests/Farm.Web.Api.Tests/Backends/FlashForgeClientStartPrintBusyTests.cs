using System.Net;
using System.Net.Sockets;
using System.Text;
using Farm.Backend.Plugin.FlashForge;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Backends;

/// <summary>
/// Behavior-level tests verifying that <see cref="FlashForgeClient.StartPrintAsync"/> correctly
/// propagates a rejected start as <see cref="PrinterBackendBusyException"/> when the ~M119
/// status response shows the printer is actively building (#317).
///
/// Each test spins up a real TCP server that replays pre-queued response strings to simulate
/// the three-connection flow: ~M601 handshake → ~M23 print-start rejection → ~M119 status check.
/// </summary>
public sealed class FlashForgeClientStartPrintBusyTests
{
    /// <summary>
    /// When the firmware rejects the ~M23 command and ~M119 reports BUILDING_FROM_SD,
    /// <see cref="FlashForgeClient.StartPrintAsync"/> must throw <see cref="PrinterBackendBusyException"/>.
    /// </summary>
    [Fact]
    public async Task StartPrintAsync_WhenM23RejectedAndM119ShowsBuildingFromSd_ThrowsPrinterBackendBusyException()
    {
        // Connection 1 (~M601 S1): handshake accepted.
        // Connection 2 (~M23 ...): firmware rejects (no "ok" in response).
        // Connection 3 (~M119): printer reports BUILDING_FROM_SD.
        var responses = new Queue<string>([
            "ok\r\n",
            "Error: printer is busy\r\n",
            "CMD M119 Received.\r\nMachineStatus: BUILDING_FROM_SD\r\nMoveMode: READY\r\nok\r\n"
        ]);

        await using var server = StartTcpServer(responses);

        var client = new FlashForgeClient(NullLogger<FlashForgeClient>.Instance, new BackendTimeoutSettings());
        Func<Task> act = () => client.StartPrintAsync($"127.0.0.1:{server.Port}", "test.gcode");

        await act.Should().ThrowAsync<PrinterBackendBusyException>(
            because: "~M23 rejection + BUILDING_FROM_SD M119 must propagate as PrinterBackendBusyException (#317)");
    }

    /// <summary>
    /// When the firmware rejects the ~M23 command and ~M119 reports BUILDING (non-SD),
    /// <see cref="FlashForgeClient.StartPrintAsync"/> must also throw <see cref="PrinterBackendBusyException"/>.
    /// </summary>
    [Fact]
    public async Task StartPrintAsync_WhenM23RejectedAndM119ShowsBuilding_ThrowsPrinterBackendBusyException()
    {
        var responses = new Queue<string>([
            "ok\r\n",
            "Error: cannot start print\r\n",
            "CMD M119 Received.\r\nMachineStatus: BUILDING\r\nMoveMode: READY\r\nok\r\n"
        ]);

        await using var server = StartTcpServer(responses);

        var client = new FlashForgeClient(NullLogger<FlashForgeClient>.Instance, new BackendTimeoutSettings());
        Func<Task> act = () => client.StartPrintAsync($"127.0.0.1:{server.Port}", "test.gcode");

        await act.Should().ThrowAsync<PrinterBackendBusyException>();
    }

    /// <summary>
    /// When the firmware rejects the ~M23 command but ~M119 reports READY (idle),
    /// <see cref="FlashForgeClient.StartPrintAsync"/> must return false rather than throw.
    /// This is the negative case — rejected for a non-busy reason.
    /// </summary>
    [Fact]
    public async Task StartPrintAsync_WhenM23RejectedAndM119ShowsReady_ReturnsFalseWithoutException()
    {
        // Connection 1: handshake. Connection 2: rejection. Connection 3: READY (not building).
        var responses = new Queue<string>([
            "ok\r\n",
            "Error: file not found\r\n",
            "CMD M119 Received.\r\nMachineStatus: READY\r\nMoveMode: READY\r\nok\r\n"
        ]);

        await using var server = StartTcpServer(responses);

        var client = new FlashForgeClient(NullLogger<FlashForgeClient>.Instance, new BackendTimeoutSettings());
        bool result = await client.StartPrintAsync($"127.0.0.1:{server.Port}", "test.gcode");

        result.Should().BeFalse(
            because: "a rejected start with a READY ~M119 response must return false, not throw PrinterBackendBusyException");
    }

    // ==================== Helper Methods ====================

    /// <summary>
    /// Starts a TCP server on a free ephemeral port that accepts one connection per
    /// queued response and writes each response string back to the client before closing
    /// the connection. Runs the accept loop in a background task.
    /// </summary>
    private static FlashForgeTcpTestServer StartTcpServer(Queue<string> responses)
    {
        int port = GetFreeTcpPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        // Process each expected connection in a background task.
        var serverTask = Task.Run(async () =>
        {
            while (responses.Count > 0)
            {
                using TcpClient connection = await listener.AcceptTcpClientAsync();
                await using NetworkStream stream = connection.GetStream();

                // Read the incoming command — discard, we respond from the queue.
                // CA2022 suppressed: test server intentionally discards command bytes.
                byte[] readBuffer = new byte[1024];
#pragma warning disable CA2022
                await stream.ReadAsync(readBuffer);
#pragma warning restore CA2022

                string response = responses.Dequeue();
                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(responseBytes);
                await stream.FlushAsync();
                // Closing the using-scoped TcpClient signals EOF to the client reader.
            }

            listener.Stop();
        });

        return new FlashForgeTcpTestServer(listener, serverTask, port);
    }

    private static int GetFreeTcpPort()
    {
        // Bind to port 0 to get an OS-assigned ephemeral port. Re-verify availability before
        // returning to reduce the TOCTOU race window in CI (port grabbed between Stop and bind).
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            try
            {
                var verify = new TcpListener(IPAddress.Loopback, port);
                verify.Start();
                verify.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Port was grabbed between allocation and verification; retry.
            }
        }

        throw new InvalidOperationException("Unable to allocate a free TCP port after 10 attempts.");
    }

    private sealed class FlashForgeTcpTestServer(TcpListener listener, Task serverTask, int port)
        : IAsyncDisposable
    {
        public int Port { get; } = port;

        public async ValueTask DisposeAsync()
        {
            try
            { listener.Stop(); }
            catch { /* already stopped */ }
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }
}
