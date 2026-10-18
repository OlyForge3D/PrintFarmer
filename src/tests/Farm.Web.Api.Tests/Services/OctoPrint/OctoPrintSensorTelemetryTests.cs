using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.OctoPrint;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OctoPrint;

/// <summary>
/// Issue #3409: OctoPrint maps every core temperature entry (<c>toolN</c>, <c>bed</c>,
/// <c>chamber</c>) onto read-only sensors and publishes them through the status cache and the
/// <c>printerupdated</c> event. Core OctoPrint reports no fan telemetry, so none is invented.
/// </summary>
public class OctoPrintSensorTelemetryTests : IDisposable
{
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Loose);
    private readonly List<PrinterStatusDto> _cacheWrites = [];
    private readonly OctoPrintWebSocketAdapter _adapter;
    private readonly MethodInfo _handleMessageAsync;

    public OctoPrintSensorTelemetryTests()
    {
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        Mock<IPrinterStatusCacheWriter> cacheWriter = new(MockBehavior.Loose);
        cacheWriter
            .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((status, _) => _cacheWrites.Add(status));

        Guid printerId = Guid.NewGuid();
        _adapter = new OctoPrintWebSocketAdapter(
            printerId,
            new Printer { Id = printerId, Name = "OctoPrint", ServerUrl = "http://octopi.local", Backend = (int)PrinterBackend.OctoPrint },
            NullLogger.Instance,
            Mock.Of<IOctoPrintClient>(),
            hub.Object,
            cacheWriter.Object);

        _handleMessageAsync = typeof(OctoPrintWebSocketAdapter)
            .GetMethod("HandleWebSocketMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public void Dispose() => _adapter.Dispose();

    [Fact]
    public void Client_AdvertisesSensorTelemetryWithoutControl()
    {
        typeof(OctoPrintClient).Should().Implement<ISupportsSensorTelemetry>();
        typeof(OctoPrintClient).Should().NotImplement<ISupportsSensorControl>(
            "core OctoPrint has no fan telemetry and sensor control is out of scope");
    }

    [Fact]
    public void ParsePrinterStatus_TempsHistory_MapsEveryEntryFromLatestSample()
    {
        using JsonDocument doc = JsonDocument.Parse(CurrentFrame(Temps(tool1Actual: 180, chamberActual: 35)));

        OctoPrintStatusData status = OctoPrintWebSocketAdapter.ParsePrinterStatus(doc.RootElement)!;

        IReadOnlyList<PrinterSensorDto> sensors = status.Sensors!;
        sensors.Select(s => s.Id).Should().BeEquivalentTo(["tool0", "tool1", "bed", "chamber"],
            "the numeric 'time' key is not a sensor and only the latest sample is used");
        sensors.Should().OnlyContain(s => s.Writable.Count == 0 && s.Available && s.FanRole == null);
        sensors.Single(s => s.Id == "tool1").Kind.Should().Be(PrinterSensorKind.Nozzle);
        sensors.Single(s => s.Id == "bed").Kind.Should().Be(PrinterSensorKind.Bed);
        PrinterSensorDto chamber = sensors.Single(s => s.Id == "chamber");
        chamber.Kind.Should().Be(PrinterSensorKind.Chamber);
        Number(chamber, PrinterSensorQuantity.Temperature).Should().Be(35);
        Number(chamber, PrinterSensorQuantity.TargetTemperature).Should().Be(40);
        Number(sensors.Single(s => s.Id == "tool1"), PrinterSensorQuantity.Temperature).Should().Be(180);

        status.HotendTemp.Should().Be(210);
        status.BedTarget.Should().Be(60);
    }

    [Fact]
    public void ParsePrinterStatus_NullChamberActual_KeepsTargetOnlyAndFrameWithoutSamplesLeavesSensorsNull()
    {
        string temps = """[{"time":1,"tool0":{"actual":210.0,"target":215.0},"chamber":{"actual":null,"target":null}}]""";
        using JsonDocument withNullChamber = JsonDocument.Parse(CurrentFrame(temps));
        OctoPrintWebSocketAdapter.ParsePrinterStatus(withNullChamber.RootElement)!.Sensors!
            .Select(s => s.Id).Should().Equal(["tool0"], "a sensor with no numeric reading is omitted rather than invented");

        using JsonDocument withoutSamples = JsonDocument.Parse(CurrentFrame("[]"));
        OctoPrintWebSocketAdapter.ParsePrinterStatus(withoutSamples.RootElement)!.Sensors.Should().BeNull();
    }

    [Fact]
    public void Map_HttpRawTemperatureDictionary_MapsEveryEntry()
    {
        Dictionary<string, object> temperatures = new()
        {
            ["tool0"] = """{"actual":200.5,"target":205.0,"offset":0}""",
            ["tool1"] = """{"actual":21.0,"target":0.0,"offset":0}""",
            ["bed"] = """{"actual":55.0,"target":60.0,"offset":0}""",
            ["chamber"] = """{"actual":30.0,"target":null,"offset":0}""",
        };

        IReadOnlyList<PrinterSensorDto> sensors = OctoPrintSensorMapper.Map(temperatures);

        sensors.Select(s => s.Id).Should().BeEquivalentTo(["tool0", "tool1", "bed", "chamber"]);
        sensors.Single(s => s.Id == "chamber").Readings.Should().ContainSingle(
            r => r.Quantity == PrinterSensorQuantity.Temperature);
        OctoPrintSensorMapper.Map((Dictionary<string, object>?)null).Should().BeEmpty();
    }

    [Fact]
    public async Task WebSocketFrame_PublishesSameSensorsToCacheAndSignalR()
    {
        await HandleAsync(CurrentFrame(Temps(tool1Actual: 180, chamberActual: 35)));

        IReadOnlyList<PrinterSensorDto> cached = _cacheWrites.Should().ContainSingle().Subject.Sensors;
        cached.Should().HaveCount(4);
        SignalRUpdates().Should().ContainSingle().Which.Sensors.Should().Equal(cached);
    }

    [Fact]
    public async Task WebSocketFrame_SensorOnlyChange_IsBroadcastAndSampleLessFrameKeepsSnapshot()
    {
        await HandleAsync(CurrentFrame(Temps(tool1Actual: 180, chamberActual: 35)));
        await HandleAsync(CurrentFrame(Temps(tool1Actual: 180, chamberActual: 35)));
        await HandleAsync(CurrentFrame(Temps(tool1Actual: 180, chamberActual: 36)));

        List<PrinterStatusUpdate> updates = SignalRUpdates();
        updates.Should().HaveCount(2, "identical frames are suppressed; a chamber-only change is published");
        Number(updates[1].Sensors.Single(s => s.Id == "chamber"), PrinterSensorQuantity.Temperature).Should().Be(36);

        await HandleAsync(CurrentFrame("[]"));

        _cacheWrites[^1].Sensors.Should().Equal(updates[1].Sensors, "a frame without samples republishes the last snapshot");
        SignalRUpdates()[^1].Sensors.Should().Equal(updates[1].Sensors);
    }

    [Fact]
    public async Task OfflineThenReconnect_MarksSensorsUnavailableThenRestoresThem()
    {
        await HandleAsync(CurrentFrame(Temps(tool1Actual: 180, chamberActual: 35)));
        IReadOnlyList<PrinterSensorDto> online = _cacheWrites[0].Sensors;

        await HandleAsync(CurrentFrame("[]", operational: false));
        await HandleAsync(CurrentFrame(Temps(tool1Actual: 180, chamberActual: 35)));

        List<PrinterStatusUpdate> updates = SignalRUpdates();
        updates.Should().HaveCount(3);
        updates[1].IsOnline.Should().BeFalse();
        updates[1].Sensors.Select(s => s.Id).Should().Equal(online.Select(s => s.Id));
        updates[1].Sensors.Should().OnlyContain(s => !s.Available, "offline clients must see the last values as stale");
        updates[1].Sensors.Single(s => s.Id == "chamber").Readings.Should().Equal(
            online.Single(s => s.Id == "chamber").Readings);
        updates[2].Sensors.Should().Equal(online);
    }

    [Fact]
    public void MarkSensorsUnavailable_PollingOfflinePath_PreservesLastValues()
    {
        using JsonDocument doc = JsonDocument.Parse(CurrentFrame(Temps(tool1Actual: 180, chamberActual: 35)));
        OctoPrintStatusData status = OctoPrintWebSocketAdapter.ParsePrinterStatus(doc.RootElement)!;
        IReadOnlyList<PrinterSensorDto> online = _adapter.ResolveSensors(status)!;

        IReadOnlyList<PrinterSensorDto> offline = _adapter.MarkSensorsUnavailable();

        offline.Should().HaveCount(online.Count).And.OnlyContain(s => !s.Available);
        offline.Select(s => s.Readings).Should().BeEquivalentTo(online.Select(s => s.Readings));
    }

    private static string Temps(double tool1Actual, double chamberActual) => $$$"""
        [
          {"time":1,"tool0":{"actual":200.0,"target":215.0},"bed":{"actual":40.0,"target":60.0}},
          {"time":2,"tool0":{"actual":210.0,"target":215.0},"tool1":{"actual":{{{tool1Actual}}},"target":185.0},
           "bed":{"actual":59.5,"target":60.0},"chamber":{"actual":{{{chamberActual}}},"target":40.0}}
        ]
        """;

    private static string CurrentFrame(string temps, bool operational = true) => $$$"""
        {"state":{"text":"x","flags":{"operational":{{{(operational ? "true" : "false")}}},"printing":false,"paused":false}},
         "temps":{{{temps}}}}
        """;

    private static double? Number(PrinterSensorDto sensor, PrinterSensorQuantity quantity) =>
        sensor.Readings.Single(r => r.Quantity == quantity).Value.Number;

    private Task HandleAsync(string currentFrame) =>
        (Task)_handleMessageAsync.Invoke(_adapter, [$$"""{"current":{{currentFrame}}}""", null, CancellationToken.None])!;

    private List<PrinterStatusUpdate> SignalRUpdates() =>
        _clientProxy.Invocations
            .Where(i => i.Method.Name == "SendCoreAsync" && (string)i.Arguments[0] == "printerupdated")
            .Select(i => (PrinterStatusUpdate)((object?[])i.Arguments[1])[0]!)
            .ToList();
}
