using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Sdcp;
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

namespace Farm.Backend.Plugins.Tests.Services.Sdcp;

/// <summary>
/// Sensor/fan telemetry pipeline tests for <c>SdcpPollingService.PollPrinterAsync</c> (#3408):
/// chamber/fan-only changes must reach SignalR and the status cache as complete snapshots,
/// partial packets must preserve previously reported sensors, and availability must track
/// offline/reconnect. Uses the same reflection harness as <see cref="SdcpPollingServiceBroadcastGateTests"/>.
/// </summary>
public sealed class SdcpPollingServiceSensorTests : IDisposable
{
    private readonly Mock<ISdcpClient> _sdcpClient = new(MockBehavior.Loose);
    private readonly Mock<IPrintersRepository> _printersRepository = new(MockBehavior.Loose);
    private readonly Mock<IUnitOfWork> _unitOfWork = new(MockBehavior.Loose);
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Loose);
    private readonly Mock<IPrinterStatusCacheWriter> _cacheWriter = new(MockBehavior.Loose);
    private readonly List<string> _groups = [];
    private readonly SdcpPollingService _service;
    private readonly MethodInfo _pollPrinterAsync;
    private readonly Guid _printerId = Guid.NewGuid();

    public SdcpPollingServiceSensorTests()
    {
        _unitOfWork.Setup(u => u.Printers).Returns(_printersRepository.Object);
        _printersRepository
            .Setup(r => r.FindByIdAsync(_printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = _printerId, Name = "Centauri", ServerUrl = "http://sdcp.local", Backend = (int)PrinterBackend.SDCP });

        Mock<IServiceProvider> serviceProvider = new(MockBehavior.Loose);
        serviceProvider.Setup(p => p.GetService(typeof(ISdcpClient))).Returns(_sdcpClient.Object);
        serviceProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_unitOfWork.Object);

        Mock<IServiceScope> scope = new(MockBehavior.Loose);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new(MockBehavior.Loose);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>()))
            .Callback<string>(_groups.Add)
            .Returns(_clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _service = new SdcpPollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<SdcpPollingService>.Instance,
            statusCacheWriter: _cacheWriter.Object);

        _pollPrinterAsync = typeof(SdcpPollingService)
            .GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task ChamberOnlyChange_IsBroadcastToAuthorizedGroupAndCached()
    {
        await RunPollsAsync(
            () => Online(Temp("TempOfBox", PrinterSensorKind.Chamber, 30)),
            () => Online(Temp("TempOfBox", PrinterSensorKind.Chamber, 32)));

        List<PrinterStatusUpdate> updates = Broadcasts();
        updates.Should().HaveCount(2, "a chamber-only change must not be suppressed by the broadcast gate");
        Reading(updates[1].Sensors, "TempOfBox").Should().Be(32);
        _groups.Should().OnlyContain(g => g == Farm.Infrastructure.Security.AuthorizedHubGroups.Printer(_printerId));
        Reading(CachedStatuses()[^1].Sensors, "TempOfBox").Should().Be(32);
    }

    [Fact]
    public async Task FanOnlyPartialPacket_PreservesPreviouslyReportedSensors()
    {
        await RunPollsAsync(
            () => Online(
                Temp("TempOfNozzle", PrinterSensorKind.Nozzle, 210),
                Temp("TempOfBox", PrinterSensorKind.Chamber, 30),
                Fan("AuxiliaryFan", PrinterFanRole.Auxiliary, 10),
                Fan("BoxFan", PrinterFanRole.Chamber, 40)) with
            { FanSpeedPercent = 75 },
            () => Online(Fan("BoxFan", PrinterFanRole.Chamber, 60)));

        List<PrinterStatusUpdate> updates = Broadcasts();
        updates.Should().HaveCount(2);
        IReadOnlyList<PrinterSensorDto>? sensors = updates[1].Sensors;
        sensors!.Select(s => s.Id).Should().Equal("AuxiliaryFan", "BoxFan", "TempOfBox", "TempOfNozzle");
        Reading(sensors, "BoxFan").Should().Be(60);
        Reading(sensors, "AuxiliaryFan").Should().Be(10);
        Reading(sensors, "TempOfBox").Should().Be(30);
        sensors.Should().OnlyContain(s => s.Available && s.Writable.Count == 0);
        updates[1].FanSpeedPercent.Should().Be(75, "an omitted ModelFan preserves the last legacy value");
        CachedStatuses()[^1].Sensors.Should().Equal(sensors);
    }

    [Fact]
    public async Task OfflineAfterFailures_MarksSensorsUnavailable_ReconnectReactivatesOnlyObserved()
    {
        int call = 0;
        await RunPollsAsync(
            () => Online(Temp("TempOfBox", PrinterSensorKind.Chamber, 30), Fan("BoxFan", PrinterFanRole.Chamber, 40)),
            () => throw new WebSocketException("refused " + ++call),
            () => throw new WebSocketException("refused " + ++call),
            () => throw new WebSocketException("refused " + ++call),
            () => Online(Temp("TempOfBox", PrinterSensorKind.Chamber, 31)));

        List<PrinterStatusUpdate> updates = Broadcasts();
        updates.Should().HaveCount(3);

        PrinterStatusUpdate offline = updates[1];
        offline.IsOnline.Should().BeFalse();
        offline.FanSpeedPercent.Should().BeNull();
        offline.Sensors!.Should().HaveCount(2).And.OnlyContain(s => !s.Available);
        Reading(offline.Sensors, "TempOfBox").Should().Be(30, "last values are kept for display while stale");

        PrinterStatusUpdate recovered = updates[2];
        recovered.Sensors!.Single(s => s.Id == "TempOfBox").Available.Should().BeTrue();
        Reading(recovered.Sensors, "TempOfBox").Should().Be(31);
        recovered.Sensors!.Single(s => s.Id == "BoxFan").Available.Should().BeFalse(
            "a sensor not observed after reconnect must stay stale rather than appear live");
    }

    [Fact]
    public async Task DirectOfflineStatus_MarksSensorsUnavailable()
    {
        await RunPollsAsync(
            () => Online(Fan("ModelFan", PrinterFanRole.PartCooling, 50)) with { FanSpeedPercent = 50 },
            () => Online() with { IsOnline = false, State = "offline" });

        PrinterStatusUpdate last = Broadcasts()[^1];
        last.FanSpeedPercent.Should().BeNull();
        last.Sensors!.Should().ContainSingle().Which.Available.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialPacket_PreservesLegacyHotendAndBedTemperatures(bool fanOnly)
    {
        await RunPollsAsync(
            () => Online(Temp("TempOfNozzle", PrinterSensorKind.Nozzle, 210)) with
            {
                HotendTemp = 210,
                HotendTarget = 215,
                BedTemp = 60,
                BedTarget = 65,
            },
            () => fanOnly
                ? Online(Fan("BoxFan", PrinterFanRole.Chamber, 60))
                : Online(Temp("TempOfBox", PrinterSensorKind.Chamber, 33)));

        PrinterStatusUpdate last = Broadcasts()[^1];
        last.HotendTemp.Should().Be(210);
        last.HotendTarget.Should().Be(215);
        last.BedTemp.Should().Be(60);
        last.BedTarget.Should().Be(65);
        PrinterStatusDto cached = CachedStatuses()[^1];
        cached.HotendTemp.Should().Be(210);
        cached.HotendTarget.Should().Be(215);
        cached.BedTemp.Should().Be(60);
        cached.BedTarget.Should().Be(65);
    }

    [Fact]
    public async Task DirectOfflineThenReconnectWithoutModelFan_DoesNotResurrectStaleLegacyTelemetry()
    {
        await RunPollsAsync(
            () => Online() with { FanSpeedPercent = 50, HotendTemp = 200 },
            () => Online() with { IsOnline = false, State = "offline" },
            () => Online(Temp("TempOfBox", PrinterSensorKind.Chamber, 30)));

        PrinterStatusUpdate recovered = Broadcasts()[^1];
        recovered.IsOnline.Should().BeTrue();
        recovered.FanSpeedPercent.Should().BeNull();
        recovered.HotendTemp.Should().BeNull();
        PrinterStatusDto cached = CachedStatuses()[^1];
        cached.FanSpeedPercent.Should().BeNull();
        cached.HotendTemp.Should().BeNull();
    }

    private static PrinterCompositeStatus Online(params PrinterSensorDto[] sensors) =>
        new(IsOnline: true, State: "IDLE", Progress: null, JobName: null, ThumbnailUrl: null, CameraStreamUrl: null, CameraSnapshotUrl: null)
        {
            Sensors = sensors,
        };

    private static PrinterSensorDto Temp(string id, PrinterSensorKind kind, double value) =>
        new(id, id, kind)
        {
            Readings = [new(PrinterSensorQuantity.Temperature, PrinterSensorValue.FromNumber(value), PrinterSensorUnit.Celsius)],
        };

    private static PrinterSensorDto Fan(string id, PrinterFanRole role, double value) =>
        new(id, id, PrinterSensorKind.Fan)
        {
            FanRole = role,
            Readings = [new(PrinterSensorQuantity.FanSpeed, PrinterSensorValue.FromNumber(value), PrinterSensorUnit.Percent)],
        };

    private static double? Reading(IReadOnlyList<PrinterSensorDto>? sensors, string id) =>
        sensors!.Single(s => s.Id == id).Readings[0].Value.Number;

    private List<PrinterStatusUpdate> Broadcasts() => _clientProxy.Invocations
        .Where(i => i.Method.Name == "SendCoreAsync" && (string)i.Arguments[0] == "printerupdated")
        .Select(i => (PrinterStatusUpdate)((object?[])i.Arguments[1])[0]!)
        .ToList();

    private List<PrinterStatusDto> CachedStatuses() => _cacheWriter.Invocations
        .Where(i => i.Method.Name == nameof(IPrinterStatusCacheWriter.UpdateStatus))
        .Select(i => (PrinterStatusDto)i.Arguments[0])
        .ToList();

    private async Task RunPollsAsync(params Func<PrinterCompositeStatus>[] polls)
    {
        using CancellationTokenSource cts = new();
        int index = 0;
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                int current = index++;
                if (current >= polls.Length - 1)
                {
                    cts.Cancel();
                }

                return Task.FromResult(polls[Math.Min(current, polls.Length - 1)]());
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [_printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation after the final scripted poll.
        }
    }
}
