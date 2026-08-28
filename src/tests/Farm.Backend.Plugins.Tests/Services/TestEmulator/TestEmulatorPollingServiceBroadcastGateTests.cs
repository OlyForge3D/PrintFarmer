using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.TestEmulator;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Services.TestEmulator;

/// <summary>
/// Unit tests for the "printerupdated" broadcast suppression gate wired into
/// <see cref="TestEmulatorPollingService"/> (issue #1355). <c>TickPrinterAsync</c> is private and
/// invoked once per call (no polling loop to work around), so it's exercised directly via
/// reflection, following the pattern in <c>PrusaLinkSyncPrinterInfoTests</c>.
/// </summary>
public class TestEmulatorPollingServiceBroadcastGateTests : IDisposable
{
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Loose);
    private readonly TestEmulatorStateManager _stateManager = new();
    private readonly TestEmulatorPollingService _service;
    private readonly MethodInfo _tickPrinterAsync;

    public TestEmulatorPollingServiceBroadcastGateTests()
    {
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        Mock<IPrinterStatusCacheWriter> statusCacheWriter = new(MockBehavior.Loose);

        _service = new TestEmulatorPollingService(
            hub.Object,
            NullLogger<TestEmulatorPollingService>.Instance,
            statusCacheWriter.Object,
            _stateManager);

        _tickPrinterAsync = typeof(TestEmulatorPollingService)
            .GetMethod("TickPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public void Dispose() => _service.Dispose();

    private Task TickAsync(Guid printerId) =>
        (Task)_tickPrinterAsync.Invoke(_service, [printerId, CancellationToken.None])!;

    private int SendCount() => _clientProxy.Invocations.Count(i => i.Method.Name == "SendCoreAsync");

    private static EmulatedPrinterState IdleState() => new()
    {
        State = EmulatorPrinterState.Idle,
    };

    [Fact]
    public async Task TickPrinterAsync_FirstTick_IsNotSuppressed()
    {
        Guid printerId = Guid.NewGuid();
        _stateManager.Register(printerId, IdleState());

        await TickAsync(printerId);

        SendCount().Should().Be(1);
    }

    [Fact]
    public async Task TickPrinterAsync_ConsecutiveIdleTicks_SecondIsSuppressed()
    {
        // Idle state never advances (no PrintStartedAt), so two consecutive ticks produce a
        // byte-identical payload — the spike's 100%-idle-suppression case.
        Guid printerId = Guid.NewGuid();
        _stateManager.Register(printerId, IdleState());

        await TickAsync(printerId);
        await TickAsync(printerId);

        SendCount().Should().Be(1, "the second idle tick is byte-identical to the first and must be suppressed");
    }

    [Fact]
    public async Task TickPrinterAsync_WhenStateChanges_SecondTickIsSent()
    {
        Guid printerId = Guid.NewGuid();
        EmulatedPrinterState state = IdleState();
        _stateManager.Register(printerId, state);

        await TickAsync(printerId);

        // Simulate a state transition between ticks (e.g. a print starting).
        state.State = EmulatorPrinterState.Printing;
        state.Progress = 0;
        state.JobName = "test-print-benchy.gcode";
        state.PrintStartedAt = DateTime.UtcNow;

        await TickAsync(printerId);

        SendCount().Should().Be(2, "a genuine state change must never be suppressed");
    }

    [Fact]
    public async Task TickPrinterAsync_OfflineThenRecovered_RecoveryTickIsNotSuppressed()
    {
        Guid printerId = Guid.NewGuid();
        EmulatedPrinterState state = new() { State = EmulatorPrinterState.Offline };
        _stateManager.Register(printerId, state);

        await TickAsync(printerId); // offline broadcast

        state.State = EmulatorPrinterState.Idle;
        await TickAsync(printerId); // recovery broadcast — must not be suppressed

        SendCount().Should().Be(2, "the first update after coming back online must never be suppressed");
    }
}
