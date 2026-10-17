using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.FlashForge;
using Farm.Infrastructure;
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

namespace Farm.Backend.Plugins.Tests.Services.FlashForge;

/// <summary>
/// Issue #3409: FlashForge maps every <c>~M105</c> extruder and the bed onto backend-neutral
/// sensors and publishes them through the status cache and the <c>printerupdated</c> event.
/// </summary>
public class FlashForgeSensorTelemetryTests : IDisposable
{
    private readonly Mock<IFlashForgeClient> _flashForgeClient = new(MockBehavior.Loose);
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Loose);
    private readonly List<PrinterStatusDto> _cacheWrites = [];
    private readonly FlashForgePollingService _service;
    private readonly MethodInfo _pollPrinterAsync;
    private readonly FieldInfo _printerStatesField;
    private readonly Type _pollingStateType;

    public FlashForgeSensorTelemetryTests()
    {
        var spoolProvider = new ManagedSpoolProviderHelper(
            new Mock<ISpoolmanStatusCache>(MockBehavior.Loose).Object,
            NullLogger<ManagedSpoolProviderHelper>.Instance);

        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        unitOfWork.Setup(u => u.Printers).Returns(new Mock<IPrintersRepository>(MockBehavior.Loose).Object);

        Mock<IServiceProvider> serviceProvider = new(MockBehavior.Loose);
        serviceProvider.Setup(p => p.GetService(typeof(IFlashForgeClient))).Returns(_flashForgeClient.Object);
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

        _service = new FlashForgePollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<FlashForgePollingService>.Instance,
            statusCacheWriter: cacheWriter.Object);

        Type serviceType = typeof(FlashForgePollingService);
        _pollPrinterAsync = serviceType.GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _printerStatesField = serviceType.GetField("_printerStates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _pollingStateType = serviceType.GetNestedType("PrinterPollingState", BindingFlags.NonPublic)!;
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public void Client_AdvertisesSensorTelemetryWithoutControl()
    {
        typeof(FlashForgeClient).Should().Implement<ISupportsSensorTelemetry>();
        typeof(FlashForgeClient).Should().NotImplement<ISupportsSensorControl>(
            "the FlashForge TCP protocol exposes no fan or chamber write command");
    }

    [Fact]
    public void Map_MultiExtruderStatus_ReportsEveryExtruderAndBedReadOnly()
    {
        IReadOnlyList<PrinterSensorDto> sensors = FlashForgeSensorMapper.Map(Status(t0: 210, t1: 180, bed: 60))!;

        sensors.Select(s => s.Id).Should().BeEquivalentTo(["T0", "T1", "bed"]);
        sensors.Should().OnlyContain(s => s.Writable.Count == 0 && s.Available && s.FanRole == null,
            "FlashForge reports no fans and offers no writable sensor");

        PrinterSensorDto t1 = sensors.Single(s => s.Id == "T1");
        t1.Kind.Should().Be(PrinterSensorKind.Nozzle);
        Number(t1, PrinterSensorQuantity.Temperature).Should().Be(180);
        Number(t1, PrinterSensorQuantity.TargetTemperature).Should().Be(185);

        PrinterSensorDto bed = sensors.Single(s => s.Id == "bed");
        bed.Kind.Should().Be(PrinterSensorKind.Bed);
        Number(bed, PrinterSensorQuantity.Temperature).Should().Be(60);
        Number(bed, PrinterSensorQuantity.TargetTemperature).Should().Be(65);
    }

    [Fact]
    public void Map_OfflineStatus_ReturnsNullSoTheCacheKeepsLastValues()
    {
        FlashForgeSensorMapper.Map(new PrinterCompositeStatus(false, null, null, null, null, null, null)).Should().BeNull();
    }

    [Fact]
    public void Map_StatusWithoutTemperatures_ReturnsEmptySnapshot()
    {
        FlashForgeSensorMapper.Map(new PrinterCompositeStatus(true, "IDLE", null, null, null, null, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task PollPrinterAsync_OnlineStatus_PublishesSameSensorsToCacheAndSignalR()
    {
        Guid printerId = Guid.NewGuid();
        SeedCachedPrinter(printerId);
        PrinterCompositeStatus status = Status(t0: 210, t1: 180, bed: 60);

        await RunOneTickAsync(printerId, status);

        IReadOnlyList<PrinterSensorDto> expected = FlashForgeSensorMapper.Map(status)!;
        _cacheWrites.Should().ContainSingle().Which.Sensors.Should().Equal(expected);
        SignalRUpdates().Should().ContainSingle().Which.Sensors.Should().Equal(expected);
    }

    [Fact]
    public async Task PollPrinterAsync_CompositeSensors_TakePrecedenceOverLegacyTemperatures()
    {
        Guid printerId = Guid.NewGuid();
        SeedCachedPrinter(printerId);
        IReadOnlyList<PrinterSensorDto> sensors = FlashForgeSensorMapper.Map(Status(t0: 210, t1: 182, bed: 60))!;
        PrinterCompositeStatus status = Status(t0: 210, t1: 180, bed: 60) with { Sensors = sensors };

        await RunOneTickAsync(printerId, status);

        _cacheWrites.Should().ContainSingle().Which.Sensors.Should().Equal(sensors);
        SignalRUpdates().Should().ContainSingle().Which.Sensors.Should().Equal(sensors);
    }

    [Fact]
    public async Task PollPrinterAsync_SensorOnlyChange_IsBroadcast()
    {
        Guid printerId = Guid.NewGuid();
        SeedCachedPrinter(printerId);

        await RunOneTickAsync(printerId, Status(t0: 210, t1: 180, bed: 60));
        await RunOneTickAsync(printerId, Status(t0: 210, t1: 180, bed: 60));
        await RunOneTickAsync(printerId, Status(t0: 210, t1: 182, bed: 60));

        List<PrinterStatusUpdate> updates = SignalRUpdates();
        updates.Should().HaveCount(2, "an identical tick is suppressed but a second-extruder-only change must be published");
        Number(updates[1].Sensors.Single(s => s.Id == "T1"), PrinterSensorQuantity.Temperature).Should().Be(182);
    }

    [Fact]
    public async Task PollPrinterAsync_OfflineStatusThenReconnect_MarksSensorsUnavailableThenRestoresThem()
    {
        Guid printerId = Guid.NewGuid();
        SeedCachedPrinter(printerId);
        PrinterCompositeStatus online = Status(t0: 210, t1: 180, bed: 60);
        IReadOnlyList<PrinterSensorDto> expected = FlashForgeSensorMapper.Map(online)!;

        await RunOneTickAsync(printerId, online);
        await RunOneTickAsync(printerId, new PrinterCompositeStatus(false, null, null, null, null, null, null));
        await RunOneTickAsync(printerId, online);

        List<PrinterStatusUpdate> updates = SignalRUpdates();
        updates.Should().HaveCount(3);
        updates[1].IsOnline.Should().BeFalse();
        updates[1].Sensors.Select(s => s.Id).Should().Equal(expected.Select(s => s.Id));
        updates[1].Sensors.Should().OnlyContain(s => !s.Available, "offline clients must see the last values as stale");
        updates[1].Sensors.Single(s => s.Id == "T0").Readings.Should().Equal(expected.Single(s => s.Id == "T0").Readings);
        updates[2].Sensors.Should().Equal(expected);
    }

    private static PrinterCompositeStatus Status(double t0, double t1, double bed) =>
        new(IsOnline: true, State: "READY", Progress: null, JobName: null, ThumbnailUrl: null, CameraStreamUrl: null, CameraSnapshotUrl: null,
            HotendTemp: t0, BedTemp: bed, HotendTarget: 215, BedTarget: 65,
            ExtruderTemperatures: new Dictionary<int, ExtruderTemperature>
            {
                [0] = new(t0, 215),
                [1] = new(t1, 185),
            },
            DetectedExtruderCount: 2);

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
        _pollingStateType.GetProperty("CachedPrinter")!.SetValue(state, new Printer
        {
            Id = printerId,
            Name = "FlashForge",
            ServerUrl = "192.168.1.50",
            Backend = (int)PrinterBackend.FlashForge,
        });
        states[printerId] = state;
    }

    private async Task RunOneTickAsync(Guid printerId, PrinterCompositeStatus status)
    {
        using CancellationTokenSource cts = new();
        _flashForgeClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
