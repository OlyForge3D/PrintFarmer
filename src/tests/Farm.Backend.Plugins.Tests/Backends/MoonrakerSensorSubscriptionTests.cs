using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>
/// Issue #3407: drives Klipper sensor payloads through the production Moonraker subscription path
/// and asserts the complete sensor snapshot reaches both the status cache and printerupdated.
/// </summary>
public sealed class MoonrakerSensorSubscriptionTests : IAsyncDisposable
{
    private readonly Guid _printerId = Guid.NewGuid();
    private readonly Mock<IPrinterStatusCacheWriter> _cacheWriter = new();
    private readonly List<PrinterStatusDto> _cached = [];
    private readonly List<(IReadOnlyList<PrinterSensorDto> Sensors, bool ReplaceAll)> _sensorWrites = [];
    private readonly List<object?> _broadcasts = [];
    private readonly ServiceProvider _provider;
    private readonly MoonrakerSubscriptionService _service;
    private readonly CancellationTokenSource _cts = new();

    public MoonrakerSensorSubscriptionTests()
    {
        _cacheWriter
            .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((status, _) => _cached.Add(status));
        _cacheWriter
            .Setup(w => w.UpdateSensors(It.IsAny<Guid>(), It.IsAny<IEnumerable<PrinterSensorDto>>(), It.IsAny<bool>()))
            .Returns<Guid, IEnumerable<PrinterSensorDto>, bool>((id, sensors, replaceAll) =>
            {
                IReadOnlyList<PrinterSensorDto> list = [.. sensors];
                _sensorWrites.Add((list, replaceAll));
                return new PrinterStatusDto(id, true, "standby") { Sensors = list };
            });

        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync("printerupdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => _broadcasts.Add(args.Single()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var services = new ServiceCollection();
        services.AddScoped(_ => new Mock<IMoonrakerClient>().Object);
        _provider = services.BuildServiceProvider();

        _service = new MoonrakerSubscriptionService(
            hub.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoonrakerSubscriptionService>.Instance,
            new Mock<IHttpClientFactory>().Object,
            _cacheWriter.Object);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _service.Dispose();
        await _provider.DisposeAsync();
        _cts.Dispose();
    }

    private Printer Printer => new() { Id = _printerId, Name = "Sensor fixture", ServerUrl = "http://sensor.invalid", Backend = (int)PrinterBackend.Moonraker };

    private async Task ProcessStatusAsync(string status)
    {
        using JsonDocument doc = JsonDocument.Parse(status);
        await _service.ProcessStatusUpdateAsync(doc.RootElement, _printerId, "http://sensor.invalid", null, null, null, null, _cts.Token);
    }

    private Task ProcessMessageAsync(string message) =>
        _service.ProcessJsonRpcMessageAsync(message, Printer, null, _cts.Token);

    private static Dictionary<string, PrinterSensorDto> ById(IReadOnlyList<PrinterSensorDto> sensors) =>
        sensors.ToDictionary(s => s.Id, StringComparer.Ordinal);

    private const string InitialStatus =
        """
        {
          "webhooks": { "state": "ready" },
          "print_stats": { "state": "standby" },
          "extruder": { "temperature": 200, "target": 210, "power": 0.4 },
          "heater_bed": { "temperature": 60, "target": 60, "power": 0.2 },
          "fan": { "speed": 0.5 },
          "temperature_sensor chamber": { "temperature": 30, "humidity": 40, "pressure": 1010 }
        }
        """;

    [Fact]
    public async Task ProcessStatusUpdateAsync_SensorPayload_PublishesCompleteSnapshotToCacheAndBroadcast()
    {
        await ProcessStatusAsync(InitialStatus);

        _cached.Should().NotBeEmpty();
        ById(_cached[^1].Sensors).Keys.Should().BeEquivalentTo("extruder", "heater_bed", "fan", "temperature_sensor chamber");
        PrinterStatusUpdate update = _broadcasts.OfType<PrinterStatusUpdate>().Last();
        update.Id.Should().Be(_printerId);
        ById(update.Sensors)["temperature_sensor chamber"].Readings.Select(r => (r.Quantity, r.Value.Number)).Should().Equal(
            (PrinterSensorQuantity.Temperature, 30d),
            (PrinterSensorQuantity.Humidity, 40d),
            (PrinterSensorQuantity.Pressure, 1010d));
        update.FanSpeedPercent.Should().Be(50);
        update.HotendTemp.Should().Be(200);
    }

    [Fact]
    public async Task ProcessStatusUpdateAsync_SensorOnlyPartialUpdate_BroadcastsMergedCompleteSnapshot()
    {
        await ProcessStatusAsync(InitialStatus);
        _broadcasts.Clear();
        _cached.Clear();

        await ProcessStatusAsync("""{ "temperature_sensor chamber": { "humidity": 45 } }""");

        PrinterStatusUpdate update = _broadcasts.OfType<PrinterStatusUpdate>().Should().ContainSingle().Subject;
        Dictionary<string, PrinterSensorDto> sensors = ById(update.Sensors);
        sensors.Keys.Should().BeEquivalentTo("extruder", "heater_bed", "fan", "temperature_sensor chamber");
        sensors["temperature_sensor chamber"].Readings.Select(r => (r.Quantity, r.Value.Number)).Should().Equal(
            (PrinterSensorQuantity.Temperature, 30d),
            (PrinterSensorQuantity.Humidity, 45d),
            (PrinterSensorQuantity.Pressure, 1010d));
        ById(_cached.Should().ContainSingle().Subject.Sensors)["temperature_sensor chamber"].Readings
            .Single(r => r.Quantity == PrinterSensorQuantity.Humidity).Value.Number.Should().Be(45);
    }

    [Fact]
    public async Task ProcessStatusUpdateAsync_TargetOnlyUpdate_BroadcastsNewTarget()
    {
        await ProcessStatusAsync(InitialStatus);
        _broadcasts.Clear();

        await ProcessStatusAsync("""{ "heater_bed": { "target": 0 } }""");

        PrinterStatusUpdate update = _broadcasts.OfType<PrinterStatusUpdate>().Last();
        ById(update.Sensors)["heater_bed"].Readings
            .Single(r => r.Quantity == PrinterSensorQuantity.TargetTemperature).Value.Number.Should().Be(0);
    }

    [Fact]
    public async Task ProcessJsonRpcMessageAsync_KlippyDisconnected_PublishesAllSensorsUnavailable()
    {
        await ProcessStatusAsync(InitialStatus);
        _broadcasts.Clear();

        await ProcessMessageAsync("""{"jsonrpc":"2.0","method":"notify_klippy_disconnected"}""");

        (IReadOnlyList<PrinterSensorDto> sensors, bool replaceAll) = _sensorWrites.Should().ContainSingle().Subject;
        replaceAll.Should().BeTrue();
        sensors.Should().HaveCount(4).And.OnlyContain(s => !s.Available && s.Readings.Count > 0);
        _broadcasts.OfType<PrinterStatusDto>().Should().ContainSingle()
            .Which.Sensors.Should().OnlyContain(s => !s.Available);
    }

    [Fact]
    public async Task ProcessJsonRpcMessageAsync_KlippyReady_RequestsObjectReenumeration()
    {
        List<string> sent = [];
        _service.RegisterJsonRpcSender(_printerId, (json, _) =>
        {
            sent.Add(json);
            return Task.CompletedTask;
        });

        await ProcessMessageAsync("""{"jsonrpc":"2.0","method":"notify_klippy_ready"}""");

        using JsonDocument request = JsonDocument.Parse(sent.Should().ContainSingle().Subject);
        request.RootElement.GetProperty("method").GetString().Should().Be("printer.objects.list");
        request.RootElement.GetProperty("id").GetInt32().Should().Be(103);
    }

    [Fact]
    public async Task ProcessJsonRpcMessageAsync_ReenumerationResponse_ResubscribesAndMarksRemovedSensorsUnavailable()
    {
        await ProcessStatusAsync(InitialStatus);
        List<string> sent = [];
        _service.RegisterJsonRpcSender(_printerId, (json, _) =>
        {
            sent.Add(json);
            return Task.CompletedTask;
        });

        await ProcessMessageAsync(
            """{"jsonrpc":"2.0","id":103,"result":{"objects":["webhooks","print_stats","extruder","heater_bed","fan","menu __main","fan_generic aux"]}}""");

        using JsonDocument request = JsonDocument.Parse(sent.Should().ContainSingle().Subject);
        request.RootElement.GetProperty("method").GetString().Should().Be("printer.objects.subscribe");
        request.RootElement.GetProperty("id").GetInt32().Should().Be(101);
        request.RootElement.GetProperty("params").GetProperty("objects").EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo("webhooks", "print_stats", "extruder", "heater_bed", "fan", "fan_generic aux");

        _broadcasts.Clear();
        await ProcessMessageAsync(
            """
            {"jsonrpc":"2.0","id":101,"result":{"eventtime":1,"status":{
              "webhooks":{"state":"ready"},"print_stats":{"state":"standby"},
              "extruder":{"temperature":201,"target":210,"power":0.4},"heater_bed":{"temperature":60,"target":60,"power":0.2},
              "fan":{"speed":0.5},"fan_generic aux":{"speed":0.25},
              "temperature_sensor chamber":{"temperature":99}
            }}}
            """);

        Dictionary<string, PrinterSensorDto> sensors = ById(_broadcasts.OfType<PrinterStatusUpdate>().Last().Sensors);
        sensors["temperature_sensor chamber"].Available.Should().BeFalse();
        sensors["temperature_sensor chamber"].Readings.Single(r => r.Quantity == PrinterSensorQuantity.Temperature).Value.Number.Should().Be(30);
        sensors["fan_generic aux"].Available.Should().BeTrue();
        sensors["fan_generic aux"].Writable.Should().ContainSingle(w => w.Quantity == PrinterSensorQuantity.FanSpeed);
        sensors["extruder"].Available.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessJsonRpcMessageAsync_MalformedReenumerationResponse_KeepsSubscription()
    {
        await ProcessStatusAsync(InitialStatus);
        List<string> sent = [];
        _service.RegisterJsonRpcSender(_printerId, (json, _) =>
        {
            sent.Add(json);
            return Task.CompletedTask;
        });

        await ProcessMessageAsync("""{"jsonrpc":"2.0","id":103,"result":{"objects":"broken"}}""");
        _broadcasts.Clear();
        await ProcessStatusAsync("""{ "fan": { "speed": 0.1 } }""");

        sent.Should().BeEmpty();
        ById(_broadcasts.OfType<PrinterStatusUpdate>().Last().Sensors).Values.Should().OnlyContain(s => s.Available);
    }
}
