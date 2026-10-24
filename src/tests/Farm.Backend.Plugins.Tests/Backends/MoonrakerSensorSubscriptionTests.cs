using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
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
    private readonly Mock<IMoonrakerClient> _client = new();
    private readonly List<PrinterStatusDto> _cached = [];
    private readonly List<(IReadOnlyList<PrinterSensorDto> Sensors, bool ReplaceAll)> _sensorWrites = [];
    private readonly List<object?> _broadcasts = [];
    private readonly ConfigurationHandler _configuration = new();
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
        services.AddScoped(_ => _client.Object);
        var printers = new Mock<IPrintersRepository>();
        printers.Setup(repository => repository.FindByIdAsync(_printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.Printers).Returns(printers.Object);
        services.AddScoped(_ => unitOfWork.Object);
        _provider = services.BuildServiceProvider();

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_configuration, disposeHandler: false));
        _service = new MoonrakerSubscriptionService(
            hub.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoonrakerSubscriptionService>.Instance,
            httpClientFactory.Object,
            _cacheWriter.Object);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _service.Dispose();
        await _provider.DisposeAsync();
        _configuration.Dispose();
        _cts.Dispose();
    }

    private Printer Printer => new()
    {
        Id = _printerId,
        Name = "Sensor fixture",
        ServerUrl = "http://sensor.invalid",
        BackendPort = 80,
        Backend = (int)PrinterBackend.Moonraker,
        Credential = PrinterCredential.FromApiKey("fixture-key"),
    };

    private async Task ProcessStatusAsync(string status)
    {
        using JsonDocument doc = JsonDocument.Parse(status);
        await _service.ProcessStatusUpdateAsync(doc.RootElement, _printerId, "http://sensor.invalid", null, null, null, null, _cts.Token);
    }

    private Task ProcessMessageAsync(string message) =>
        _service.ProcessJsonRpcMessageAsync(message, Printer, null, _cts.Token);

    private static Dictionary<string, PrinterSensorDto> ById(IReadOnlyList<PrinterSensorDto> sensors) =>
        sensors.ToDictionary(s => s.Id, StringComparer.Ordinal);

    private static readonly List<string> HeaterObjects =
        ["configfile", "webhooks", "print_stats", "toolhead", "extruder", "extruder1", "extruder2", "extruder3", "heater_bed", "fan"];

    private const string HeaterSettings =
        """
        {"result":{"status":{"configfile":{"settings":{
          "extruder":{"min_temp":0,"max_temp":250},
          "extruder1":{"min_temp":0,"max_temp":260},
          "extruder2":{"min_temp":10,"max_temp":270},
          "extruder3":{"min_temp":0,"max_temp":280},
          "heater_bed":{"min_temp":0,"max_temp":100}
        }}}}}
        """;

    private const string HeaterSubscriptionStatus =
        """
        {
          "webhooks":{"state":"ready"},"print_stats":{"state":"standby"},
          "toolhead":{"extruder":"extruder2"},
          "configfile":{"warnings":[],"save_config_pending":false,"save_config_pending_items":{}},
          "extruder":{"temperature":25,"target":0},
          "extruder1":{"temperature":26,"target":0},
          "extruder2":{"temperature":27,"target":0},
          "extruder3":{"temperature":28,"target":0},
          "heater_bed":{"temperature":29,"target":0},
          "fan":{"speed":0.5}
        }
        """;

    private Task InitializeHeatersAsync() =>
        _service.SubscribeToObjectsAsync(Printer, HeaterObjects, (_, _) => Task.CompletedTask, _cts.Token);

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
    public async Task ProcessStatusUpdateAsync_RuntimeFactors_PreservesIndependentDeltasAndInvalidatesMalformedValues()
    {
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"},"print_stats":{"state":"printing"},"gcode_move":{"speed_factor":1.25,"extrude_factor":0.875}}""");
        Assert.Equal(125, _cached[^1].SpeedFactorPercent);
        Assert.Equal(87.5, _cached[^1].FlowFactorPercent);
        await ProcessStatusAsync("""{"gcode_move":{"speed_factor":0.8}}""");
        Assert.Equal(80, _cached[^1].SpeedFactorPercent);
        Assert.Equal(87.5, _cached[^1].FlowFactorPercent);
        await ProcessStatusAsync("""{"fan":{"speed":0.4}}""");
        Assert.Equal(80, _cached[^1].SpeedFactorPercent);
        Assert.Equal(87.5, _cached[^1].FlowFactorPercent);
        await ProcessStatusAsync("""{"gcode_move":{"extrude_factor":1.1}}""");
        PrinterStatusUpdate update = _broadcasts.OfType<PrinterStatusUpdate>().Last();
        Assert.Equal(80, update.SpeedFactorPercent);
        Assert.Equal(110, update.FlowFactorPercent!.Value, precision: 10);
        Assert.Equal(update.SpeedFactorPercent, update.ToStatusDto().SpeedFactorPercent);
        Assert.Equal(update.FlowFactorPercent, update.ToStatusDto().FlowFactorPercent);
        await ProcessStatusAsync("""{"gcode_move":{"speed_factor":null,"extrude_factor":"NaN"}}""");
        Assert.Null(_cached[^1].SpeedFactorPercent);
        Assert.Null(_cached[^1].FlowFactorPercent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"speed_factor":"NaN","extrude_factor":"Infinity"}""")]
    [InlineData("""{"speed_factor":-1,"extrude_factor":0}""")]
    [InlineData("""{"speed_factor":1e309,"extrude_factor":1e308}""")]
    [InlineData("""{"speed_factor":false,"extrude_factor":[]}""")]
    [InlineData("null")]
    public async Task ProcessStatusUpdateAsync_UnknownRuntimeFactors_NeverInventsDefaults(string factors)
    {
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"},"gcode_move":""" + factors + "}");
        Assert.Null(_cached[^1].SpeedFactorPercent);
        Assert.Null(_cached[^1].FlowFactorPercent);
        PrinterStatusUpdate update = _broadcasts.OfType<PrinterStatusUpdate>().Last();
        Assert.Null(update.SpeedFactorPercent);
        Assert.Null(update.FlowFactorPercent);
    }

    [Fact]
    public async Task SubscribeToObjectsAsync_Reconnect_DropsOldFactorsUntilNewSnapshot()
    {
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"},"gcode_move":{"speed_factor":1.25,"extrude_factor":0.875}}""");
        List<string> requests = [];
        await _service.SubscribeToObjectsAsync(Printer, ["gcode_move", "webhooks"], (json, _) =>
        {
            requests.Add(json);
            return Task.CompletedTask;
        }, _cts.Token);
        using JsonDocument request = JsonDocument.Parse(Assert.Single(requests));
        Assert.Equal(JsonValueKind.Null, request.RootElement.GetProperty("params").GetProperty("objects").GetProperty("gcode_move").ValueKind);
        await ProcessMessageAsync("""{"jsonrpc":"2.0","id":101,"result":{"status":{"webhooks":{"state":"ready"},"gcode_move":{"speed_factor":0.7}}}}""");
        Assert.Equal(70, _cached[^1].SpeedFactorPercent);
        Assert.Null(_cached[^1].FlowFactorPercent);
    }

    [Fact]
    public async Task MarkSensorsUnavailableAsync_TransportLost_DoesNotResurrectFactorsFromPartialUpdates()
    {
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"},"gcode_move":{"speed_factor":1.25,"extrude_factor":0.875}}""");
        await _service.MarkSensorsUnavailableAsync(_printerId, _cts.Token);
        _cacheWriter.Verify(w => w.ClearRuntimeFactors(_printerId), Times.Once);
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"}}""");
        Assert.Null(_cached[^1].SpeedFactorPercent);
        Assert.Null(_cached[^1].FlowFactorPercent);
    }

    [Fact]
    public async Task ProcessStatusUpdateAsync_KlippyShutdown_DiscardsLastFactorObservations()
    {
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"},"gcode_move":{"speed_factor":1.25,"extrude_factor":0.875}}""");
        await ProcessStatusAsync("""{"webhooks":{"state":"shutdown"}}""");
        Assert.Null(_cached[^1].SpeedFactorPercent);
        Assert.Null(_cached[^1].FlowFactorPercent);
    }

    [Theory]
    [InlineData(false, 80d, 95d)]
    [InlineData(true, 80d, 95d)]
    [InlineData(false, null, 95d)]
    [InlineData(true, null, 95d)]
    [InlineData(false, 80d, null)]
    [InlineData(true, 80d, null)]
    public async Task HttpRuntimeFactors_FactorFreeDelta_PreservesHttpObservationsAndIndependentUnknowns(
        bool refresh, double? speed, double? flow)
    {
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"},"gcode_move":{"speed_factor":1.25,"extrude_factor":0.875}}""");
        _client.Setup(client => client.GetCompositeStatusAsync(
                It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FactorStatus(speed, flow));

        await ReadHttpFactorsAsync(refresh);
        AssertRuntimeFactors(speed, flow);
        await ProcessStatusAsync("""{"fan":{"speed":0.4},"extruder":{"temperature":201}}""");
        AssertRuntimeFactors(speed, flow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpRuntimeFactors_DelayedResponse_PreservesNewerIndependentWebSocketObservation(bool refresh)
    {
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"},"gcode_move":{"speed_factor":1.25,"extrude_factor":0.875}}""");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<PrinterCompositeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(client => client.GetCompositeStatusAsync(
                It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .Callback(() => started.SetResult())
            .Returns(response.Task);

        Task olderRead = ReadHttpFactorsAsync(refresh);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ProcessStatusAsync("""{"gcode_move":{"speed_factor":0.6}}""");
        response.SetResult(FactorStatus(80, 95));
        await olderRead;

        AssertRuntimeFactors(60, 95);
        await ProcessStatusAsync("""{"fan":{"speed":0.4}}""");
        AssertRuntimeFactors(60, 95);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpRuntimeFactors_DelayedResponse_DoesNotReplaceNewerHttpObservationOrInvalidation(bool refresh)
    {
        await ProcessStatusAsync("""{"webhooks":{"state":"ready"},"gcode_move":{"speed_factor":1.25,"extrude_factor":0.875}}""");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<PrinterCompositeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(client => client.GetCompositeStatusAsync(
                It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .Callback(() => started.SetResult())
            .Returns(response.Task);

        Task olderRead = ReadHttpFactorsAsync(refresh);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _client.Setup(client => client.GetCompositeStatusAsync(
                It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FactorStatus(75, null));
        await ReadHttpFactorsAsync(!refresh);
        AssertRuntimeFactors(75, null);
        response.SetResult(FactorStatus(80, 95));
        await olderRead;

        AssertRuntimeFactors(75, null);
        await ProcessStatusAsync("""{"fan":{"speed":0.4}}""");
        AssertRuntimeFactors(75, null);
    }

    private static PrinterCompositeStatus FactorStatus(double? speed, double? flow) =>
        new(true, "printing", null, null, null, null, null, SpeedFactorPercent: speed, FlowFactorPercent: flow);

    private Task ReadHttpFactorsAsync(bool refresh) => refresh
        ? _service.RefreshPrinterStatusAsync(_printerId, delayMs: 0, _cts.Token)
        : _service.TriggerHttpPollingFallbackAsync(Printer, _cts.Token);

    private void AssertRuntimeFactors(double? speed, double? flow)
    {
        Assert.Equal(speed, _cached[^1].SpeedFactorPercent);
        Assert.Equal(flow, _cached[^1].FlowFactorPercent);
        PrinterStatusUpdate update = _broadcasts.OfType<PrinterStatusUpdate>().Last();
        Assert.Equal(speed, update.SpeedFactorPercent);
        Assert.Equal(flow, update.FlowFactorPercent);
    }

    [Fact]
    public async Task ProcessStatusUpdateAsync_FourTools_UsesActiveIdentityAndKeepsUnrelatedUpdatesFromRefreshingHeaterEvidence()
    {
        await ProcessStatusAsync(
            """
            {
              "webhooks":{"state":"ready"},"print_stats":{"state":"printing"},
              "toolhead":{"extruder":"extruder3"},
              "extruder":{"temperature":200,"target":210},
              "extruder1":{"temperature":201,"target":211},
              "extruder2":{"temperature":202,"target":212},
              "extruder3":{"temperature":203,"target":213},
              "heater_bed":{"temperature":60,"target":65}
            }
            """);
        PrinterStatusDto initial = _cached[^1];
        initial.ActiveExtruderId.Should().Be("extruder3");
        initial.HotendTemp.Should().Be(203);
        initial.HotendTarget.Should().Be(213);
        DateTime? observed = initial.SafetyTelemetry!.MeasuredHotendTemperatureC.ObservedAtUtc;
        initial.SafetyTelemetry.MeasuredHotendTemperatureC.Source.Should().Be("moonraker:extruder3.temperature");

        await ProcessStatusAsync("""{"fan":{"speed":0.7}}""");

        _cached[^1].HotendTemp.Should().Be(203);
        _cached[^1].SafetyTelemetry!.MeasuredHotendTemperatureC.ObservedAtUtc.Should().Be(observed);
        await ProcessStatusAsync("""{"toolhead":{"extruder":"extruder1"}}""");
        PrinterStatusUpdate switched = _broadcasts.OfType<PrinterStatusUpdate>().Last();
        switched.ActiveExtruderId.Should().Be("extruder1");
        switched.HotendTemp.Should().Be(201);
        switched.HotendTarget.Should().Be(211);

        await ProcessStatusAsync("""{"extruder1":{"temperature":null,"target":0}}""");
        _cached[^1].HotendTemp.Should().BeNull();
        _cached[^1].HotendTarget.Should().Be(0);
        _cached[^1].SafetyTelemetry!.MeasuredHotendTemperatureC.Value.Should().BeNull();
    }

    [Fact]
    public async Task TriggerHttpPollingFallbackAsync_FreshRestSensors_ReplacesStaleUnavailableSnapshot()
    {
        await ProcessStatusAsync(InitialStatus);
        DateTime now = DateTime.UtcNow;
        PrinterSensorDto sensor = new("extruder3", "Tool 3", PrinterSensorKind.Nozzle)
        {
            Readings = [new PrinterSensorReadingDto(PrinterSensorQuantity.Temperature, PrinterSensorValue.FromNumber(203d), PrinterSensorUnit.Celsius)],
            LastUpdated = now,
        };
        _client
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterCompositeStatus(true, "printing", null, null, null, null, null,
                HotendTemp: 203, HotendTarget: 213, ActiveExtruderId: "extruder3", SpeedFactorPercent: 135, FlowFactorPercent: 92)
            { Sensors = [sensor] });
        _broadcasts.Clear();
        _cached.Clear();

        await _service.TriggerHttpPollingFallbackAsync(Printer, _cts.Token);

        PrinterStatusUpdate update = _broadcasts.OfType<PrinterStatusUpdate>().Should().ContainSingle().Subject;
        update.Sensors.Should().ContainSingle().Which.Should().BeEquivalentTo(sensor);
        update.ActiveExtruderId.Should().Be("extruder3");
        Assert.Equal(135, update.SpeedFactorPercent);
        Assert.Equal(92, update.FlowFactorPercent);
        Assert.Equal(135, _cached[0].SpeedFactorPercent);
        Assert.Equal(92, _cached[0].FlowFactorPercent);
        _cached.Should().ContainSingle().Which.SensorsReported.Should().BeTrue();
        _cached[0].Sensors.Should().BeEquivalentTo(update.Sensors);
    }

    [Fact]
    public async Task SubscribeToObjectsAsync_SubscriptionOmitsStaticSettings_LoadsExactLimitsBeforeSubscribing()
    {
        _configuration.Body = HeaterSettings;
        List<string> sent = [];
        await _service.SubscribeToObjectsAsync(Printer, HeaterObjects, (json, _) =>
        {
            _configuration.Requests.Should().ContainSingle();
            sent.Add(json);
            return Task.CompletedTask;
        }, _cts.Token);
        await ProcessMessageAsync(
            """{"jsonrpc":"2.0","id":101,"result":{"status":""" + HeaterSubscriptionStatus + "}}");
        await ProcessMessageAsync(
            """{"jsonrpc":"2.0","method":"notify_status_update","params":[{"extruder2":{"temperature":30}},1]}""");

        _configuration.Requests.Should().Equal((HttpMethod.Get, "/printer/objects/query?configfile=settings", "fixture-key"));
        using JsonDocument request = JsonDocument.Parse(sent.Should().ContainSingle().Subject);
        request.RootElement.GetProperty("method").GetString().Should().Be("printer.objects.subscribe");
        request.RootElement.GetProperty("params").GetProperty("objects").GetProperty("extruder3").ValueKind.Should().Be(JsonValueKind.Null);
        Dictionary<string, PrinterSensorDto> sensors = ById(_cached[^1].Sensors);
        foreach ((string id, double min, double max) in new[]
        {
            ("extruder", 0d, 250d), ("extruder1", 0d, 260d), ("extruder2", 10d, 270d),
            ("extruder3", 0d, 280d), ("heater_bed", 0d, 100d),
        })
        {
            sensors[id].Writable.Should().Equal(
                new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, min, max, null, PrinterSensorUnit.Celsius) { AllowZero = true });
        }

        _cached[^1].ActiveExtruderId.Should().Be("extruder2");
        _cached[^1].HotendTemp.Should().Be(30);
        _cached[^1].HotendTarget.Should().Be(0);
        _broadcasts.OfType<PrinterStatusUpdate>().Last().Sensors.Should().BeEquivalentTo(_cached[^1].Sensors);
    }

    [Fact]
    public async Task ProcessStatusUpdateAsync_HttpFallbackThenWebSocketDelta_PreservesConfiguredWritableLimits()
    {
        _configuration.Body = HeaterSettings;
        await InitializeHeatersAsync();
        await ProcessStatusAsync(HeaterSubscriptionStatus);
        IReadOnlyList<PrinterSensorDto> restSensors = _cached[^1].Sensors;
        restSensors.Where(sensor => sensor.Kind is PrinterSensorKind.Nozzle or PrinterSensorKind.Bed)
            .Should().HaveCount(5).And.OnlyContain(sensor => sensor.Writable.Count == 1);
        _client
            .Setup(client => client.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterCompositeStatus(true, "standby", null, null, null, null, null) { Sensors = restSensors });
        await _service.TriggerHttpPollingFallbackAsync(Printer, _cts.Token);
        _cached[^1].Sensors.Should().BeEquivalentTo(restSensors);

        await ProcessStatusAsync("""{"extruder2":{"temperature":31}}""");
        await ProcessStatusAsync("""{"fan":{"speed":0.75}}""");

        _cached[^1].Sensors.Select(sensor => (sensor.Id, sensor.Writable))
            .Should().BeEquivalentTo(restSensors.Select(sensor => (sensor.Id, sensor.Writable)));
        ById(_cached[^1].Sensors)["extruder2"].Readings
            .Single(reading => reading.Quantity == PrinterSensorQuantity.Temperature).Value.Number.Should().Be(31);
        _broadcasts.OfType<PrinterStatusUpdate>().Last().Sensors.Should().BeEquivalentTo(_cached[^1].Sensors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscribeToObjectsAsync_TwoPrinters_KeepsConfiguredLimitsIsolated(bool secondConfigurationFails)
    {
        _configuration.Body = HeaterSettings;
        await InitializeHeatersAsync();
        await ProcessStatusAsync(HeaterSubscriptionStatus);
        Printer second = Printer;
        second.Id = Guid.NewGuid();
        second.ServerUrl = "http://second-sensor.invalid";
        _configuration.Body = HeaterSettings.Replace("\"max_temp\":270", "\"max_temp\":180", StringComparison.Ordinal);
        _configuration.StatusCode = secondConfigurationFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;

        await _service.SubscribeToObjectsAsync(second, HeaterObjects, (_, _) => Task.CompletedTask, _cts.Token);
        await _service.ProcessJsonRpcMessageAsync(
            """{"jsonrpc":"2.0","id":101,"result":{"status":""" + HeaterSubscriptionStatus + "}}",
            second, null, _cts.Token);
        PrinterSensorDto secondHeater = ById(_cached[^1].Sensors)["extruder2"];
        _cached[^1].Id.Should().Be(second.Id);
        if (secondConfigurationFails)
        {
            secondHeater.Writable.Should().BeEmpty();
        }
        else
        {
            secondHeater.Writable.Should().ContainSingle(descriptor => descriptor.Min == 10 && descriptor.Max == 180);
        }

        await ProcessStatusAsync("""{"fan":{"speed":0.75}}""");
        _cached[^1].Id.Should().Be(_printerId);
        ById(_cached[^1].Sensors)["extruder2"].Writable.Should()
            .ContainSingle(descriptor => descriptor.Min == 10 && descriptor.Max == 270);
    }

    [Fact]
    public async Task ProcessJsonRpcMessageAsync_Rediscovery_RevokesOldLimitsAndLoadsChangedConfiguration()
    {
        _configuration.Body = HeaterSettings;
        await InitializeHeatersAsync();
        await ProcessStatusAsync(HeaterSubscriptionStatus);
        List<string> sent = [];
        _service.RegisterJsonRpcSender(_printerId, (json, _) =>
        {
            sent.Add(json);
            return Task.CompletedTask;
        });
        await ProcessMessageAsync("""{"jsonrpc":"2.0","method":"notify_klippy_disconnected"}""");
        _sensorWrites[^1].Sensors.Where(sensor => sensor.Kind is PrinterSensorKind.Nozzle or PrinterSensorKind.Bed)
            .Should().OnlyContain(sensor => !sensor.Available && sensor.Writable.Count == 0);
        _configuration.Body = HeaterSettings.Replace("\"max_temp\":270", "\"max_temp\":180", StringComparison.Ordinal);

        await ProcessMessageAsync("""{"jsonrpc":"2.0","method":"notify_klippy_ready"}""");
        await ProcessMessageAsync(
            """{"jsonrpc":"2.0","id":103,"result":{"objects":""" + JsonSerializer.Serialize(HeaterObjects) + "}}");
        await ProcessStatusAsync(HeaterSubscriptionStatus);
        await ProcessStatusAsync("""{"extruder2":{"temperature":32}}""");

        _configuration.Requests.Should().HaveCount(2);
        ById(_cached[^1].Sensors)["extruder2"].Writable.Should()
            .ContainSingle(descriptor => descriptor.Min == 10 && descriptor.Max == 180 && descriptor.AllowZero);
        sent.Select(json => JsonDocument.Parse(json).RootElement.GetProperty("method").GetString())
            .Should().Equal("printer.objects.list", "printer.objects.subscribe");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>proxy login</html>")]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.OK, """{"result":{"status":{}}}""")]
    [InlineData(HttpStatusCode.OK, """{"result":{"status":{"configfile":{"settings":null}}}}""")]
    [InlineData(HttpStatusCode.OK, """{"result":{"status":{"configfile":{"settings":[]}}}}""")]
    [InlineData(HttpStatusCode.OK, """{"result":{"status":{"configfile":{"settings":{"extruder2":{"min_temp":0,"max_temp":"300"}}}}}}""")]
    public async Task SubscribeToObjectsAsync_ConfigurationFailsAfterReconnect_ReportsReadingsWithoutStaleControls(
        HttpStatusCode status, string body)
    {
        _configuration.Body = HeaterSettings;
        await InitializeHeatersAsync();
        await ProcessStatusAsync(HeaterSubscriptionStatus);
        _configuration.StatusCode = status;
        _configuration.Body = body;

        await InitializeHeatersAsync();
        await ProcessStatusAsync(HeaterSubscriptionStatus);
        await ProcessStatusAsync("""{"fan":{"speed":0.25}}""");

        _configuration.Requests.Should().HaveCount(2);
        _cached[^1].Sensors.Where(sensor => sensor.Kind is PrinterSensorKind.Nozzle or PrinterSensorKind.Bed)
            .Should().HaveCount(5).And.OnlyContain(sensor => sensor.Available && sensor.Writable.Count == 0);
        ById(_cached[^1].Sensors)["fan"].Writable.Should().ContainSingle();
        _sensorWrites[^1].Sensors.Should().OnlyContain(sensor => !sensor.Available);
        _broadcasts.OfType<PrinterStatusUpdate>().Last().Sensors.Should().BeEquivalentTo(_cached[^1].Sensors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscribeToObjectsAsync_ConfigurationTransportFails_KeepsTelemetryReadOnly(bool timeout)
    {
        _configuration.Respond = (_, _) =>
        {
            if (timeout)
            {
                throw new OperationCanceledException();
            }

            throw new HttpRequestException("Fixture transport failure");
        };

        await InitializeHeatersAsync();
        await ProcessStatusAsync(HeaterSubscriptionStatus);

        _cached[^1].Sensors.Where(sensor => sensor.Kind is PrinterSensorKind.Nozzle or PrinterSensorKind.Bed)
            .Should().HaveCount(5).And.OnlyContain(sensor => sensor.Available && sensor.Writable.Count == 0);
    }

    [Fact]
    public async Task SubscribeToObjectsAsync_CancelledDuringConfigurationRead_DoesNotSubscribeOrRestoreLimits()
    {
        _configuration.Respond = (_, _) =>
        {
            _cts.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(HeaterSettings) });
        };
        List<string> sent = [];

        Func<Task> initialize = () => _service.SubscribeToObjectsAsync(Printer, HeaterObjects, (json, _) =>
        {
            sent.Add(json);
            return Task.CompletedTask;
        }, _cts.Token);
        await initialize.Should().ThrowAsync<OperationCanceledException>();

        sent.Should().BeEmpty();
        _cached.Should().BeEmpty();
        _broadcasts.Should().BeEmpty();
    }

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
    public async Task NotifyStatusUpdate_TemperatureHumidityAndFanOnlyFixtures_BroadcastMergedSnapshotsAndRestoreAfterReconnect()
    {
        await ProcessStatusAsync(InitialStatus);
        List<string> sent = [];
        _service.RegisterJsonRpcSender(_printerId, (json, _) =>
        {
            sent.Add(json);
            return Task.CompletedTask;
        });

        _broadcasts.Clear();
        await ProcessMessageAsync(
            """{"jsonrpc":"2.0","method":"notify_status_update","params":[{"temperature_sensor chamber":{"temperature":31.5}},10.0]}""");
        await ProcessMessageAsync(
            """{"jsonrpc":"2.0","method":"notify_status_update","params":[{"temperature_sensor chamber":{"humidity":42}},10.5]}""");
        await ProcessMessageAsync(
            """{"jsonrpc":"2.0","method":"notify_status_update","params":[{"fan":{"speed":0.25}},11.0]}""");

        List<PrinterStatusUpdate> updates = [.. _broadcasts.OfType<PrinterStatusUpdate>()];
        updates.Should().HaveCount(3);
        Dictionary<string, PrinterSensorDto> last = ById(updates[^1].Sensors);
        last.Keys.Should().BeEquivalentTo("extruder", "heater_bed", "fan", "temperature_sensor chamber");
        last["temperature_sensor chamber"].Readings.Select(r => (r.Quantity, r.Value.Number)).Should().Equal(
            (PrinterSensorQuantity.Temperature, 31.5d),
            (PrinterSensorQuantity.Humidity, 42d),
            (PrinterSensorQuantity.Pressure, 1010d));
        last["fan"].Readings.Single(r => r.Quantity == PrinterSensorQuantity.FanSpeed).Value.Number.Should().Be(25);
        last["extruder"].Readings.Single(r => r.Quantity == PrinterSensorQuantity.Temperature).Value.Number.Should().Be(200);
        updates[^1].FanSpeedPercent.Should().Be(25);

        await ProcessMessageAsync("""{"jsonrpc":"2.0","method":"notify_klippy_disconnected"}""");
        _sensorWrites[^1].Sensors.Should().OnlyContain(s => !s.Available);

        await ProcessMessageAsync("""{"jsonrpc":"2.0","method":"notify_klippy_ready"}""");
        await ProcessMessageAsync(
            """{"jsonrpc":"2.0","id":103,"result":{"objects":["webhooks","print_stats","extruder","heater_bed","fan","temperature_sensor chamber"]}}""");
        sent.Select(s => JsonDocument.Parse(s).RootElement.GetProperty("method").GetString()).Should()
            .Equal("printer.objects.list", "printer.objects.subscribe");

        _broadcasts.Clear();
        await ProcessMessageAsync(
            """
            {"jsonrpc":"2.0","id":101,"result":{"eventtime":20,"status":{
              "webhooks":{"state":"ready"},"print_stats":{"state":"standby"},
              "extruder":{"temperature":190,"target":0,"power":0},"heater_bed":{"temperature":55,"target":0,"power":0},
              "fan":{"speed":0},"temperature_sensor chamber":{"temperature":29,"humidity":50,"pressure":1005}
            }}}
            """);

        Dictionary<string, PrinterSensorDto> restored = ById(_broadcasts.OfType<PrinterStatusUpdate>().Last().Sensors);
        restored.Values.Should().OnlyContain(s => s.Available);
        restored.Keys.Should().BeEquivalentTo("extruder", "heater_bed", "fan", "temperature_sensor chamber");
        restored["temperature_sensor chamber"].Readings.Select(r => (r.Quantity, r.Value.Number)).Should().Equal(
            (PrinterSensorQuantity.Temperature, 29d),
            (PrinterSensorQuantity.Humidity, 50d),
            (PrinterSensorQuantity.Pressure, 1005d));
    }

    [Fact]
    public async Task TriggerHttpPollingFallbackAsync_TrackedSensors_BroadcastsThemUnavailableWithoutLosingWebSocketState()
    {
        await ProcessStatusAsync(InitialStatus);
        _client
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterCompositeStatus(true, "standby", null, null, null, null, null, HotendTemp: 201, BedTemp: 60));
        _broadcasts.Clear();
        _cached.Clear();

        await _service.TriggerHttpPollingFallbackAsync(Printer, _cts.Token);

        PrinterStatusUpdate fallback = _broadcasts.OfType<PrinterStatusUpdate>().Should().ContainSingle().Subject;
        fallback.Sensors.Should().HaveCount(4).And.OnlyContain(s => !s.Available && s.Readings.Count > 0);
        fallback.HotendTemp.Should().Be(201);
        _cached.Should().ContainSingle().Which.SensorsReported.Should().BeTrue();
        _cached[0].Sensors.Should().BeEquivalentTo(fallback.Sensors);

        _broadcasts.Clear();
        await ProcessStatusAsync("""{ "fan": { "speed": 0.1 } }""");

        ById(_broadcasts.OfType<PrinterStatusUpdate>().Last().Sensors).Values.Should().OnlyContain(s => s.Available);
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

    private sealed class ConfigurationHandler : HttpMessageHandler
    {
        public string Body { get; set; } = """{"result":{"status":{"configfile":{"settings":{}}}}}""";

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }

        public List<(HttpMethod Method, string Path, string? ApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values) ? values.Single() : null));
            return Respond is not null
                ? Respond(request, cancellationToken)
                : Task.FromResult(new HttpResponseMessage(StatusCode) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
}
