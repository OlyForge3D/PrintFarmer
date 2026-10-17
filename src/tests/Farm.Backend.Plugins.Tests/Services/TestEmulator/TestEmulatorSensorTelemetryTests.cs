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
/// Issue #3409: the test emulator exposes a deterministic sensor set (chamber, environment,
/// filament, controllable and read-only fans) with writable controls, published through the
/// status cache and the <c>printerupdated</c> event.
/// </summary>
public class TestEmulatorSensorTelemetryTests : IDisposable
{
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Loose);
    private readonly List<PrinterStatusDto> _cacheWrites = [];
    private readonly TestEmulatorStateManager _stateManager = new();
    private readonly TestEmulatorPollingService _service;
    private readonly TestEmulatorClient _client;
    private readonly MethodInfo _tickPrinterAsync;

    public TestEmulatorSensorTelemetryTests()
    {
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        Mock<IPrinterStatusCacheWriter> cacheWriter = new(MockBehavior.Loose);
        cacheWriter
            .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((status, _) => _cacheWrites.Add(status));

        _service = new TestEmulatorPollingService(
            hub.Object,
            NullLogger<TestEmulatorPollingService>.Instance,
            cacheWriter.Object,
            _stateManager);
        _client = new TestEmulatorClient(_stateManager);

        _tickPrinterAsync = typeof(TestEmulatorPollingService)
            .GetMethod("TickPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public void Client_AdvertisesSensorTelemetryAndControl()
    {
        typeof(TestEmulatorClient).Should().Implement<ISupportsSensorTelemetry>();
        typeof(TestEmulatorClient).Should().Implement<ISupportsSensorControl>();
    }

    [Fact]
    public void GetSensors_IdleState_ReportsDeterministicSensorSet()
    {
        EmulatedPrinterState state = new() { State = EmulatorPrinterState.Idle, HotendFanRpm = 3200 };

        IReadOnlyList<PrinterSensorDto> sensors = state.GetSensors(21.5, 20.5);

        sensors.Select(s => s.Id).Should().BeEquivalentTo(
            ["nozzle", "bed", "chamber", "environment", "filament", "fan_part", "fan_hotend"]);
        sensors.Should().OnlyContain(s => s.Available);
        state.GetSensors(21.5, 20.5).Should().Equal(sensors, "identical state must produce content-equal snapshots");

        PrinterSensorDto chamber = sensors.Single(s => s.Id == "chamber");
        chamber.Kind.Should().Be(PrinterSensorKind.Chamber);
        Number(chamber, PrinterSensorQuantity.Temperature).Should().Be(25);
        chamber.Writable.Should().ContainSingle().Which.Should().Be(
            new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, 0, 60, 1, PrinterSensorUnit.Celsius));

        PrinterSensorDto environment = sensors.Single(s => s.Id == "environment");
        environment.Readings.Should().BeEquivalentTo(
        [
            new PrinterSensorReadingDto(PrinterSensorQuantity.Humidity, PrinterSensorValue.FromNumber(40), PrinterSensorUnit.Percent),
            new PrinterSensorReadingDto(PrinterSensorQuantity.Pressure, PrinterSensorValue.FromNumber(1013.25), PrinterSensorUnit.HectoPascal),
        ]);
        environment.Writable.Should().BeEmpty();

        PrinterSensorDto filament = sensors.Single(s => s.Id == "filament");
        filament.Kind.Should().Be(PrinterSensorKind.FilamentRunout);
        filament.Readings.Should().ContainSingle().Which.Value.Should().Be(PrinterSensorValue.FromBoolean(true));

        PrinterSensorDto partFan = sensors.Single(s => s.Id == "fan_part");
        partFan.FanRole.Should().Be(PrinterFanRole.PartCooling);
        partFan.Writable.Should().ContainSingle().Which.Quantity.Should().Be(PrinterSensorQuantity.FanSpeed);

        PrinterSensorDto hotendFan = sensors.Single(s => s.Id == "fan_hotend");
        hotendFan.FanRole.Should().Be(PrinterFanRole.Hotend);
        hotendFan.Writable.Should().BeEmpty("the hotend fan is firmware-controlled");
        Number(hotendFan, PrinterSensorQuantity.FanRpm).Should().Be(3200);
    }

    [Fact]
    public void GetSensors_OfflineState_MarksSensorsUnavailableButKeepsValues()
    {
        EmulatedPrinterState state = new() { State = EmulatorPrinterState.Offline };

        IReadOnlyList<PrinterSensorDto> sensors = state.GetSensors(20, 20);

        sensors.Should().HaveCount(7).And.OnlyContain(s => !s.Available);
        Number(sensors.Single(s => s.Id == "chamber"), PrinterSensorQuantity.Temperature).Should().Be(25);
    }

    [Fact]
    public async Task TickPrinterAsync_PublishesSameSensorsToCacheAndSignalR()
    {
        Guid printerId = Guid.NewGuid();
        _stateManager.Register(printerId, new EmulatedPrinterState { State = EmulatorPrinterState.Idle });

        await TickAsync(printerId);

        IReadOnlyList<PrinterSensorDto> cached = _cacheWrites.Should().ContainSingle().Subject.Sensors;
        cached.Should().HaveCount(7);
        SignalRUpdates().Should().ContainSingle().Which.Sensors.Should().Equal(cached);
    }

    [Fact]
    public async Task TickPrinterAsync_SensorOnlyChangeViaControl_IsBroadcast()
    {
        Guid printerId = Guid.NewGuid();
        _stateManager.Register(printerId, new EmulatedPrinterState { State = EmulatorPrinterState.Idle });

        await TickAsync(printerId);
        await TickAsync(printerId);
        (await _client.SetSensorValueAsync(BaseUrl(printerId), "chamber", PrinterSensorQuantity.TargetTemperature, 45, null))
            .Should().BeTrue();
        await TickAsync(printerId);

        List<PrinterStatusUpdate> updates = SignalRUpdates();
        updates.Should().HaveCount(2, "an identical tick is suppressed but a chamber-target-only change must be published");
        Number(updates[1].Sensors.Single(s => s.Id == "chamber"), PrinterSensorQuantity.TargetTemperature).Should().Be(45);
        Number(_cacheWrites[^1].Sensors.Single(s => s.Id == "chamber"), PrinterSensorQuantity.TargetTemperature).Should().Be(45);
    }

    [Fact]
    public async Task TickPrinterAsync_OfflineThenReconnect_MarksSensorsUnavailableThenRestoresThem()
    {
        Guid printerId = Guid.NewGuid();
        EmulatedPrinterState state = new() { State = EmulatorPrinterState.Idle };
        _stateManager.Register(printerId, state);

        await TickAsync(printerId);
        state.State = EmulatorPrinterState.Offline;
        await TickAsync(printerId);
        state.State = EmulatorPrinterState.Idle;
        await TickAsync(printerId);

        List<PrinterStatusUpdate> updates = SignalRUpdates();
        updates.Should().HaveCount(3);
        updates[1].IsOnline.Should().BeFalse();
        updates[1].Sensors.Should().HaveCount(7).And.OnlyContain(s => !s.Available);
        updates[2].Sensors.Should().Equal(updates[0].Sensors);
    }

    [Fact]
    public async Task SetSensorValueAsync_PartFan_UpdatesState()
    {
        Guid printerId = Guid.NewGuid();
        EmulatedPrinterState state = new() { State = EmulatorPrinterState.Idle };
        _stateManager.Register(printerId, state);

        (await _client.SetSensorValueAsync(BaseUrl(printerId), "fan_part", PrinterSensorQuantity.FanSpeed, 75, null))
            .Should().BeTrue();

        state.PartCoolingFanPercent.Should().Be(75);
    }

    [Theory]
    [InlineData("chamber", PrinterSensorQuantity.TargetTemperature, 61)]
    [InlineData("fan_part", PrinterSensorQuantity.FanSpeed, -1)]
    [InlineData("fan_hotend", PrinterSensorQuantity.FanRpm, 1000)]
    [InlineData("environment", PrinterSensorQuantity.Humidity, 50)]
    [InlineData("unknown", PrinterSensorQuantity.Temperature, 20)]
    public async Task SetSensorValueAsync_InvalidOrReadOnlyTarget_IsRejectedWithoutChangingState(
        string sensorId, PrinterSensorQuantity quantity, double value)
    {
        Guid printerId = Guid.NewGuid();
        EmulatedPrinterState state = new() { State = EmulatorPrinterState.Idle };
        _stateManager.Register(printerId, state);
        IReadOnlyList<PrinterSensorDto> before = state.GetSensors(20, 20);

        Func<Task> act = () => _client.SetSensorValueAsync(BaseUrl(printerId), sensorId, quantity, value, null);

        await act.Should().ThrowAsync<ArgumentException>();
        state.GetSensors(20, 20).Should().Equal(before);
    }

    [Fact]
    public async Task SetSensorValueAsync_UnknownPrinter_Throws()
    {
        Func<Task> act = () => _client.SetSensorValueAsync(
            BaseUrl(Guid.NewGuid()), "chamber", PrinterSensorQuantity.TargetTemperature, 40, null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static string BaseUrl(Guid printerId) => $"http://testemulator-{printerId}";

    private static double? Number(PrinterSensorDto sensor, PrinterSensorQuantity quantity) =>
        sensor.Readings.Single(r => r.Quantity == quantity).Value.Number;

    private Task TickAsync(Guid printerId) =>
        (Task)_tickPrinterAsync.Invoke(_service, [printerId, CancellationToken.None])!;

    private List<PrinterStatusUpdate> SignalRUpdates() =>
        _clientProxy.Invocations
            .Where(i => i.Method.Name == "SendCoreAsync" && (string)i.Arguments[0] == "printerupdated")
            .Select(i => (PrinterStatusUpdate)((object?[])i.Arguments[1])[0]!)
            .ToList();
}
