using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

[SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "Test-owned TCS signals coordinate the fake transport and have no UI synchronization context.")]
public sealed class MoonrakerMotionChannelTests
{
    [Fact]
    public void HandshakeUri_RemovesQueryCredentialsAndUserInfo()
    {
        Uri endpoint = MoonrakerMotionChannelFactory.BuildWebSocketUri(new Uri("https://fixture:example@host.invalid:7125/old?token=fixture#fragment"));
        Assert.Equal("wss://host.invalid:7125/websocket", endpoint.AbsoluteUri);
        Assert.Empty(endpoint.Query);
        Assert.Empty(endpoint.UserInfo);
    }

    [Theory]
    [InlineData("xyz", "[50,60,10,0]", true)]
    [InlineData("xyz", "[0,0,0,0]", true)]
    [InlineData("", "[50,60,10,0]", true)]
    [InlineData("xyz", "[null,60,10,0]", false)]
    [InlineData("xyz", "[50,60]", false)]
    public async Task ReadMotionStateAsync_UsesOneFreshGcodeFrameWithoutActuation(string axes, string position, bool valid)
    {
        var socket = new FakeSocket();
        await using var channel = new MoonrakerMotionChannel(socket);
        DateTime before = DateTime.UtcNow;
        Task<PrinterStatusDto> query = channel.ReadMotionStateAsync(Guid.NewGuid(), default);
        await socket.Sent.Task;
        using JsonDocument request = JsonDocument.Parse(socket.LastSend);
        Assert.Equal("printer.objects.query", request.RootElement.GetProperty("method").GetString());
        Assert.Contains("gcode_position", Encoding.UTF8.GetString(socket.LastSend), StringComparison.Ordinal);
        string id = request.RootElement.GetProperty("id").GetString()!;
        string reply = """
            {"id":"REQUEST_ID","result":{"status":{"webhooks":{"state":"ready"},"print_stats":{"state":"standby"},"toolhead":{"homed_axes":"AXES"},"gcode_move":{"gcode_position":POSITION,"position":[50,60,10,0],"homing_origin":[0,0,0,0]}}}}
            """.Replace("REQUEST_ID", id, StringComparison.Ordinal).Replace("AXES", axes, StringComparison.Ordinal).Replace("POSITION", position, StringComparison.Ordinal);
        socket.Enqueue(reply);
        if (valid)
        {
            PrinterStatusDto state = await query;
            using JsonDocument expected = JsonDocument.Parse(position);
            Assert.Equal(expected.RootElement[0].GetDouble(), state.X);
            Assert.Equal(expected.RootElement[1].GetDouble(), state.Y);
            Assert.Equal(expected.RootElement[2].GetDouble(), state.Z);
            Assert.Equal(axes.Length, state.SafetyTelemetry!.HomedAxes.Value!.Length);
            Assert.Equal(50 - state.X, state.SafetyTelemetry.CoordinateOriginOffsetMm.Value!.X);
            Assert.Equal(60 - state.Y, state.SafetyTelemetry.CoordinateOriginOffsetMm.Value.Y);
            Assert.Equal(10 - state.Z, state.SafetyTelemetry.CoordinateOriginOffsetMm.Value.Z);
            Assert.InRange(state.SafetyTelemetry.HomedAxes.ObservedAtUtc!.Value, before, DateTime.UtcNow);
            Assert.Equal(state.SafetyTelemetry.HomedAxes.ObservedAtUtc, state.SafetyTelemetry.CoordinateOriginOffsetMm.ObservedAtUtc);
        }
        else
        {
            Assert.Equal("printer_safety_evidence_unknown", (await Assert.ThrowsAsync<PrinterControlException>(() => query)).Code);
        }

        Assert.Equal(1, socket.SendCount);
    }

    [Fact]
    public void BuildScript_MoveTo_RestoresCoordinateAndFeedStateWithoutRestoreMovement()
    {
        string script = PrinterControlIntent.BuildScript(new(PrinterControlKind.MoveTo, 10, 20, 30, 1200));
        Assert.Equal("SAVE_GCODE_STATE NAME=printfarmer_motion\nG90\nG1 X10 Y20 Z30 F1200\nRESTORE_GCODE_STATE NAME=printfarmer_motion MOVE=0\nM400", script);
        Assert.False(PrinterControlIntent.IsValid(new(PrinterControlKind.MoveTo, 10)));
    }

    [Fact]
    public async Task ExecuteAsync_LongHomingWithUnrelatedFrames_WaitsBeyondOldDeadlineForMatchingResponse()
    {
        var socket = new FakeSocket();
        await using var channel = new MoonrakerMotionChannel(socket);
        Guid id = Guid.NewGuid();
        Task operation = channel.ExecuteAsync(id, "G28\nM400", default);
        await socket.Sent.Task;
        using JsonDocument request = JsonDocument.Parse(socket.LastSend);
        Assert.Equal("printer.gcode.script", request.RootElement.GetProperty("method").GetString());
        Assert.Equal(id.ToString(), request.RootElement.GetProperty("id").GetString());
        Assert.Equal("G28\nM400", request.RootElement.GetProperty("params").GetProperty("script").GetString());
        for (int i = 0; i < 12; i++)
        {
            socket.Enqueue("{\"method\":\"notify_status_update\",\"params\":[]}");
        }
        socket.Enqueue($"{{\"id\":\"{Guid.NewGuid()}\",\"result\":\"ok\"}}");
        await Task.Delay(TimeSpan.FromSeconds(28));
        Assert.False(operation.IsCompleted);
        socket.Enqueue($"{{\"id\":\"{id}\",", false);
        socket.Enqueue("\"result\":\"ok\"}", true);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_RpcErrorAfterPartialMotion_DoesNotReportSuccess()
    {
        var socket = new FakeSocket();
        await using var channel = new MoonrakerMotionChannel(socket);
        Guid id = Guid.NewGuid();
        Task operation = channel.ExecuteAsync(id, "G28\nM400", default);
        await socket.Sent.Task;
        socket.Enqueue($"{{\"id\":\"{id}\",\"error\":{{\"code\":400,\"message\":\"private macro detail\"}}}}");
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.DoesNotContain("private macro detail", error.Message);
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousWrite_DoesNotRetry()
    {
        var socket = new FakeSocket { FailWrite = true };
        await using var channel = new MoonrakerMotionChannel(socket);
        await Assert.ThrowsAsync<WebSocketException>(() => channel.ExecuteAsync(Guid.NewGuid(), "G28\nM400", default));
        Assert.Equal(1, socket.SendCount);
    }

    [Fact]
    public async Task DisposeAsync_InFlightCommand_AbortsAndJoinsWithoutReplay()
    {
        var socket = new FakeSocket();
        var channel = new MoonrakerMotionChannel(socket);
        Task operation = channel.ExecuteAsync(Guid.NewGuid(), "G28\nM400", default);
        await socket.Sent.Task;
        await channel.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => operation);
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.Equal(1, socket.SendCount);
    }

    [Theory]
    [InlineData(PrinterControlKind.HomeAll, "G28\nM400")]
    [InlineData(PrinterControlKind.HomeXY, "G28 X Y\nM400")]
    [InlineData(PrinterControlKind.HomeZ, "G28 Z\nM400")]
    public void BuildScript_Homes_EndsWithQueueDrain(PrinterControlKind kind, string expected) =>
        Assert.Equal(expected, PrinterControlIntent.BuildScript(new(kind)));

    [Fact]
    public void BuildScript_Jog_RestoresModeBeforeDrainAndUsesInvariantNumbers()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string script = PrinterControlIntent.BuildScript(new(PrinterControlKind.Jog, X: -1.25, F: 600));
            Assert.Contains("G91\nG1 X-1.25 F600", script);
            Assert.EndsWith("RESTORE_GCODE_STATE NAME=printfarmer_motion MOVE=0\nM400", script, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void IsValid_MalformedMovementAndHomeFields_Rejects()
    {
        Assert.False(PrinterControlIntent.IsValid(new(PrinterControlKind.HomeAll, X: 1)));
        Assert.False(PrinterControlIntent.IsValid(new(PrinterControlKind.Jog)));
        Assert.False(PrinterControlIntent.IsValid(new(PrinterControlKind.MoveTo, X: double.NaN)));
        Assert.False(PrinterControlIntent.IsValid(new(PrinterControlKind.MoveTo, X: double.PositiveInfinity)));
        Assert.False(PrinterControlIntent.IsValid(new(PrinterControlKind.Jog, X: 1, F: 0)));
        Assert.False(PrinterControlIntent.IsValid(new((PrinterControlKind)99)));
        Assert.True(PrinterControlIntent.IsValid(new(PrinterControlKind.Jog, X: 0)));
        Assert.Equal(PrinterControlIntent.Normalize(new(PrinterControlKind.Jog, X: 0)),
            PrinterControlIntent.Normalize(new(PrinterControlKind.Jog, X: -0.0)));
    }

    [Fact]
    public void BuildScript_SmallFiniteAxis_IsNotRoundedToZeroOrWrittenAsExponent()
    {
        string script = PrinterControlIntent.BuildScript(new(PrinterControlKind.Jog, X: 1e-20));
        Assert.Contains("X0.00000000000000000001", script);
        Assert.DoesNotContain("E-", script);
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<(byte[] Data, bool End)> frames = Channel.CreateUnbounded<(byte[], bool)>();
        private WebSocketState state = WebSocketState.Open;
        public bool FailWrite { get; init; }
        public int SendCount { get; private set; }
        public byte[] LastSend { get; private set; } = [];
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public void Enqueue(string text, bool end = true) => frames.Writer.TryWrite((Encoding.UTF8.GetBytes(text), end));
        public override void Abort() => state = WebSocketState.Aborted;
        [SuppressMessage("Usage", "IDISP010:Call base.Dispose", Justification = "WebSocket.Dispose is abstract; this in-memory fake has no native resources.")]
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            (byte[] data, bool end) = await frames.Reader.ReadAsync(cancellationToken);
            data.CopyTo(buffer.AsSpan());
            return new(data.Length, WebSocketMessageType.Text, end);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendCount++;
            LastSend = buffer.ToArray();
            Sent.TrySetResult();
            if (FailWrite)
            {
                throw new WebSocketException("Ambiguous write.");
            }
            return Task.CompletedTask;
        }
    }
}
