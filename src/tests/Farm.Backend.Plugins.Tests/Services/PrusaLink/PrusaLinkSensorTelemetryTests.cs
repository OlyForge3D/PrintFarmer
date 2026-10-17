using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Services.PrusaLink;

/// <summary>
/// Issue #3409: PrusaLink maps the v1 status printer block onto backend-neutral sensors and
/// publishes them through the status cache and the <c>printerupdated</c> SignalR event.
/// </summary>
public class PrusaLinkSensorTelemetryTests : IDisposable
{
    private readonly Mock<IPrusaLinkClient> _prusaLinkClient = new(MockBehavior.Loose);
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Loose);
    private readonly List<PrinterStatusDto> _cacheWrites = [];
    private readonly PrusaLinkPollingService _service;
    private readonly MethodInfo _pollPrinterAsync;
    private readonly FieldInfo _printerStatesField;
    private readonly Type _pollingStateType;

    public PrusaLinkSensorTelemetryTests()
    {
        var spoolProvider = new ManagedSpoolProviderHelper(
            new Mock<ISpoolmanStatusCache>(MockBehavior.Loose).Object,
            NullLogger<ManagedSpoolProviderHelper>.Instance);

        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        unitOfWork.Setup(u => u.Printers).Returns(new Mock<IPrintersRepository>(MockBehavior.Loose).Object);

        Mock<IServiceProvider> serviceProvider = new(MockBehavior.Loose);
        serviceProvider.Setup(p => p.GetService(typeof(IPrusaLinkClient))).Returns(_prusaLinkClient.Object);
        serviceProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(unitOfWork.Object);
        serviceProvider.Setup(p => p.GetService(typeof(ManagedSpoolProviderHelper))).Returns(spoolProvider);

        Mock<IServiceScope> scope = new(MockBehavior.Loose);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);
        Mock<IServiceScopeFactory> scopeFactory = new(MockBehavior.Loose);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        Mock<IPrinterStatusCacheWriter> cacheWriter = new(MockBehavior.Loose);
        cacheWriter
            .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((status, _) => _cacheWrites.Add(status));

        _service = new PrusaLinkPollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<PrusaLinkPollingService>.Instance,
            statusCacheWriter: cacheWriter.Object);

        Type serviceType = typeof(PrusaLinkPollingService);
        _pollPrinterAsync = serviceType.GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _printerStatesField = serviceType.GetField("_printerStates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _pollingStateType = serviceType.GetNestedType("PrinterPollingState", BindingFlags.NonPublic)!;
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public void Client_AdvertisesSensorTelemetryWithoutControl()
    {
        typeof(PrusaLinkClient).Should().Implement<ISupportsSensorTelemetry>();
        typeof(PrusaLinkClient).Should().NotImplement<ISupportsSensorControl>(
            "PrusaLink has no fan-speed or chamber write endpoint");
    }

    [Fact]
    public void Map_StatusPrinter_ReportsNozzleBedAndBothFansReadOnly()
    {
        IReadOnlyList<PrinterSensorDto> sensors = PrusaLinkSensorMapper.Map(PrinterInfo(fanPrint: 4200, fanHotend: 5100));

        sensors.Select(s => s.Id).Should().BeEquivalentTo(["nozzle", "bed", "fan_print", "fan_hotend"]);
        sensors.Should().OnlyContain(s => s.Writable.Count == 0 && s.Available);

        PrinterSensorDto nozzle = sensors.Single(s => s.Id == "nozzle");
        nozzle.Kind.Should().Be(PrinterSensorKind.Nozzle);
        Number(nozzle, PrinterSensorQuantity.Temperature).Should().Be(214.9);
        Number(nozzle, PrinterSensorQuantity.TargetTemperature).Should().Be(215);

        PrinterSensorDto bed = sensors.Single(s => s.Id == "bed");
        bed.Kind.Should().Be(PrinterSensorKind.Bed);
        Number(bed, PrinterSensorQuantity.Temperature).Should().Be(59.5);
        Number(bed, PrinterSensorQuantity.TargetTemperature).Should().Be(60);

        // Buddy firmware reports fan_print/fan_hotend as tachometer RPM, not a duty-cycle percentage.
        PrinterSensorDto printFan = sensors.Single(s => s.Id == "fan_print");
        printFan.FanRole.Should().Be(PrinterFanRole.PartCooling);
        printFan.Readings.Should().ContainSingle().Which.Should().Be(
            new PrinterSensorReadingDto(PrinterSensorQuantity.FanRpm, PrinterSensorValue.FromNumber(4200), PrinterSensorUnit.Rpm));

        PrinterSensorDto hotendFan = sensors.Single(s => s.Id == "fan_hotend");
        hotendFan.FanRole.Should().Be(PrinterFanRole.Hotend);
        Number(hotendFan, PrinterSensorQuantity.FanRpm).Should().Be(5100);
    }

    [Fact]
    public void Map_MissingOptionalFields_OmitsThemInsteadOfInventingValues()
    {
        IReadOnlyList<PrinterSensorDto> sensors = PrusaLinkSensorMapper.Map(new StatusPrinterInfo
        {
            State = "IDLE",
            TempNozzle = 24.5,
        });

        sensors.Should().ContainSingle().Which.Id.Should().Be("nozzle");
        sensors[0].Readings.Should().ContainSingle(r => r.Quantity == PrinterSensorQuantity.Temperature);
        PrusaLinkSensorMapper.Map(null).Should().BeEmpty();
    }

    [Fact]
    public async Task PollPrinterAsync_OnlineStatus_PublishesSameSensorsToCacheAndSignalR()
    {
        Guid printerId = Guid.NewGuid();
        SeedCachedPrinter(printerId);
        IReadOnlyList<PrinterSensorDto> sensors = PrusaLinkSensorMapper.Map(PrinterInfo(fanPrint: 3000, fanHotend: 4000));

        await RunOneTickAsync(printerId, OnlineStatus(sensors));

        _cacheWrites.Should().ContainSingle().Which.Sensors.Should().Equal(sensors);
        PrinterStatusUpdate update = SignalRUpdates().Should().ContainSingle().Subject;
        update.Sensors.Should().Equal(sensors);
    }

    [Fact]
    public async Task PollPrinterAsync_SensorOnlyChange_IsBroadcast()
    {
        Guid printerId = Guid.NewGuid();
        SeedCachedPrinter(printerId);

        await RunOneTickAsync(printerId, OnlineStatus(PrusaLinkSensorMapper.Map(PrinterInfo(fanPrint: 0, fanHotend: 4000))));
        await RunOneTickAsync(printerId, OnlineStatus(PrusaLinkSensorMapper.Map(PrinterInfo(fanPrint: 0, fanHotend: 4000))));
        await RunOneTickAsync(printerId, OnlineStatus(PrusaLinkSensorMapper.Map(PrinterInfo(fanPrint: 2500, fanHotend: 4000))));

        List<PrinterStatusUpdate> updates = SignalRUpdates();
        updates.Should().HaveCount(2, "an identical tick is suppressed but a fan-only change must be published");
        Number(updates[1].Sensors.Single(s => s.Id == "fan_print"), PrinterSensorQuantity.FanRpm).Should().Be(2500);
    }

    [Fact]
    public async Task PollPrinterAsync_OfflineStatusThenReconnect_MarksSensorsUnavailableThenRestoresThem()
    {
        Guid printerId = Guid.NewGuid();
        SeedCachedPrinter(printerId);
        IReadOnlyList<PrinterSensorDto> sensors = PrusaLinkSensorMapper.Map(PrinterInfo(fanPrint: 3000, fanHotend: 4000));

        await RunOneTickAsync(printerId, OnlineStatus(sensors));
        await RunOneTickAsync(printerId, new PrusaCompositeStatus(false, null, null, null, null, null, null));
        await RunOneTickAsync(printerId, OnlineStatus(sensors));

        List<PrinterStatusUpdate> updates = SignalRUpdates();
        updates.Should().HaveCount(3);
        updates[1].IsOnline.Should().BeFalse();
        updates[1].Sensors.Select(s => s.Id).Should().Equal(sensors.Select(s => s.Id));
        updates[1].Sensors.Should().OnlyContain(s => !s.Available, "offline clients must see the last values as stale");
        updates[1].Sensors.Single(s => s.Id == "nozzle").Readings.Should().Equal(sensors.Single(s => s.Id == "nozzle").Readings);
        _cacheWrites[1].Sensors.Should().BeEmpty("the offline cache write leaves sensors unassigned so the cache keeps and marks them");
        updates[2].Sensors.Should().Equal(sensors);
    }

    private static StatusPrinterInfo PrinterInfo(int fanPrint, int fanHotend) => new()
    {
        State = "PRINTING",
        TempNozzle = 214.9,
        TargetNozzle = 215,
        TempBed = 59.5,
        TargetBed = 60,
        FanPrint = fanPrint,
        FanHotend = fanHotend,
    };

    private static PrusaCompositeStatus OnlineStatus(IReadOnlyList<PrinterSensorDto> sensors) =>
        new(IsOnline: true, State: "IDLE", Progress: null, JobName: null, ThumbnailUrl: null, CameraStreamUrl: null, CameraSnapshotUrl: null)
        {
            Sensors = sensors,
        };

    private static double? Number(PrinterSensorDto sensor, PrinterSensorQuantity quantity) =>
        sensor.Readings.Single(r => r.Quantity == quantity).Value.Number;

    private List<PrinterStatusUpdate> SignalRUpdates() =>
        _clientProxy.Invocations
            .Where(i => i.Method.Name == "SendCoreAsync" && (string)i.Arguments[0] == "printerupdated")
            .Select(i => (PrinterStatusUpdate)((object?[])i.Arguments[1])[0]!)
            .ToList();

    private void SeedCachedPrinter(Guid printerId)
    {
        var states = (System.Collections.IDictionary)_printerStatesField.GetValue(_service)!;
        object state = Activator.CreateInstance(_pollingStateType)!;
        _pollingStateType.GetProperty("PrinterId")!.SetValue(state, printerId);
        _pollingStateType.GetProperty("LastKnownIsOnline")!.SetValue(state, false);
        _pollingStateType.GetProperty("HasSyncedPrinterInfo")!.SetValue(state, true);
        _pollingStateType.GetProperty("CachedPrinter")!.SetValue(state, new Printer
        {
            Id = printerId,
            Name = "Prusa",
            ServerUrl = "http://prusa.local",
            Backend = (int)PrinterBackend.PrusaLink,
        });
        states[printerId] = state;
    }

    private async Task RunOneTickAsync(Guid printerId, PrusaCompositeStatus status)
    {
        using CancellationTokenSource cts = new();
        _prusaLinkClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromResult(status);
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation once the single tick completes.
        }
    }
}
