using System.Collections.Concurrent;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Proves the real, unchanged <see cref="MoonrakerSubscriptionService"/> can identify,
/// discover objects, subscribe, and process a status update against the emulator over a
/// genuine WebSocket connection — not a mock of the subscription service, the actual
/// production hosted service. Only its public surface is used
/// (<see cref="MoonrakerSubscriptionService.StartAsync"/> /
/// <c>StopAsync</c>/<c>Dispose</c>): the internal <c>SubscriptionLoopOverride</c> escape
/// hatch and <c>EnumerateAndStartSubscriptionsAsync</c> are only <c>[InternalsVisibleTo]</c>
/// for <c>Farm.Web.Api.Tests</c>, not this project, so the service's *real*
/// <c>SubscribePrinterLoopAsync</c> — the one that actually opens the WebSocket — runs
/// unmodified here.
///
/// The repository/hub/cache dependencies below are test doubles standing in for EF Core
/// and SignalR (neither of which the emulator's own process needs), but every piece of
/// Moonraker wire-protocol behavior (identify, printer.objects.list, printer.objects.subscribe,
/// the initial status snapshot, and later notify_status_update broadcasts) is exercised by
/// the real client code, against the real emulator, over a real socket.
/// </summary>
public sealed class RealMoonrakerSubscriptionServiceIntegrationTests : IClassFixture<RealEmulatorHost>, IAsyncDisposable
{
    private readonly RealEmulatorHost _host;
    private readonly ITestOutputHelper _output;
    private MoonrakerSubscriptionService? _service;

    public RealMoonrakerSubscriptionServiceIntegrationTests(RealEmulatorHost host, ITestOutputHelper output)
    {
        _host = host;
        _output = output;
    }
    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }
    }

    [Fact]
    public async Task StartAsync_IdentifiesSubscribesAndProcessesInitialStatusSnapshot()
    {
        await _host.ResetAsync();
        var printer = BuildPrinterPointingAtEmulator();

        var printersRepo = new Mock<IPrintersRepository>();
        printersRepo
            .Setup(r => r.GetByBackendWithToolheadsAsync(PrinterBackend.Moonraker, It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        printersRepo
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.Printers).Returns(printersRepo.Object);

        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => unitOfWork.Object);
        services.AddScoped<IMoonrakerClient>(
            _ => new MoonrakerClient(new HttpClient(), NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()));
        await using ServiceProvider provider = services.BuildServiceProvider();

        var clientProxy = new Mock<IClientProxy>();
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hubContext = new Mock<IHubContext<PrinterHub>>();
        hubContext.SetupGet(h => h.Clients).Returns(hubClients.Object);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        var observedUpdates = new ConcurrentQueue<PrinterStatusDto>();
        var firstUpdate = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusCacheWriter = new Mock<IPrinterStatusCacheWriter>();
        statusCacheWriter
            .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((dto, _) =>
            {
                observedUpdates.Enqueue(dto);
                firstUpdate.TrySetResult(dto);
            });

        _service = new MoonrakerSubscriptionService(
            hubContext.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOutputLogger<MoonrakerSubscriptionService>(_output),
            httpClientFactory.Object,
            statusCacheWriter.Object);

        await _service.StartAsync(CancellationToken.None);

        PrinterStatusDto initial = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));
        initial.Id.Should().Be(printer.Id);

        // The initial cache write proves server.connection.identify, printer.objects.list,
        // and printer.objects.subscribe all round-tripped over the real WebSocket and the
        // subscription acknowledgement's initial status snapshot was parsed and applied —
        // and the SignalR broadcast that accompanies it went out on the real hub context.
        clientProxy.Verify(
            p => p.SendCoreAsync("printerupdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());

        // Now prove an *ongoing* notify_status_update (not just the initial subscribe ack)
        // is also received: mutate the printer through the emulator's own REST surface and
        // wait for a second cache write reflecting it.
        var secondUpdate = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        statusCacheWriter
            .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((dto, _) =>
            {
                observedUpdates.Enqueue(dto);
                secondUpdate.TrySetResult(dto);
            });

        using HttpResponseMessage scenario = await _host.ControlClient.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json("""{"scenario":"Printing"}"""));
        scenario.EnsureSuccessStatusCode();

        PrinterStatusDto updated = await secondUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));
        updated.Id.Should().Be(printer.Id);

        var shutdownUpdate = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        statusCacheWriter
            .Setup(w => w.UpdateStatus(
                It.Is<PrinterStatusDto>(dto => dto.State == "Shutdown"),
                It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((dto, _) =>
            {
                observedUpdates.Enqueue(dto);
                shutdownUpdate.TrySetResult(dto);
            });

        using HttpResponseMessage emergencyStop = await _host.ControlClient.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json("""{"script":"M112"}"""));
        emergencyStop.EnsureSuccessStatusCode();

        PrinterStatusDto shutdown = await shutdownUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));
        shutdown.IsOnline.Should().BeTrue(
            "Moonraker remains reachable when only Klippy has entered shutdown");

        var recoveredUpdate = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        statusCacheWriter
            .Setup(w => w.UpdateStatus(
                It.Is<PrinterStatusDto>(dto => dto.State == "Idle" && dto.IsOnline),
                It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((dto, _) =>
            {
                observedUpdates.Enqueue(dto);
                recoveredUpdate.TrySetResult(dto);
            });

        using HttpResponseMessage firmwareRestart = await _host.ControlClient.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json("""{"script":"FIRMWARE_RESTART"}"""));
        firmwareRestart.EnsureSuccessStatusCode();

        PrinterStatusDto recovered = await recoveredUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));
        recovered.Id.Should().Be(printer.Id);
        observedUpdates
            .Where(dto => dto.State is "Shutdown" or "Error")
            .Should()
            .OnlyContain(dto => dto.IsOnline);
        observedUpdates.Should().NotContain(dto => dto.State == "Error");
    }

    [Fact]
    public async Task MmuHappyHareMode_ObjectSnapshot_IsParsedIntoMmuStatusDto()
    {
        await _host.ResetAsync();

        // Switch MMU mode *before* starting the subscription service: the real service
        // discovers subscribable objects via printer.objects.list once at startup, so "mmu"
        // must already be present in that discovery snapshot to be included in the
        // subscription (and therefore in every status update that follows, including the
        // very first one).
        using HttpResponseMessage mmuMode = await _host.ControlClient.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"HappyHare"}"""));
        mmuMode.EnsureSuccessStatusCode();

        try
        {
            var printer = BuildPrinterPointingAtEmulator();

            var printersRepo = new Mock<IPrintersRepository>();
            printersRepo
                .Setup(r => r.GetByBackendWithToolheadsAsync(PrinterBackend.Moonraker, It.IsAny<CancellationToken>()))
                .ReturnsAsync([printer]);
            printersRepo
                .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(printer);

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.Printers).Returns(printersRepo.Object);

            var services = new ServiceCollection();
            services.AddScoped<IUnitOfWork>(_ => unitOfWork.Object);
            services.AddScoped<IMoonrakerClient>(
                _ => new MoonrakerClient(new HttpClient(), NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()));
            await using ServiceProvider provider = services.BuildServiceProvider();

            var clientProxy = new Mock<IClientProxy>();
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
            var hubContext = new Mock<IHubContext<PrinterHub>>();
            hubContext.SetupGet(h => h.Clients).Returns(hubClients.Object);

            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

            var firstUpdate = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusCacheWriter = new Mock<IPrinterStatusCacheWriter>();
            statusCacheWriter
                .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
                .Callback<PrinterStatusDto, long?>((dto, _) => firstUpdate.TrySetResult(dto));

            _service = new MoonrakerSubscriptionService(
                hubContext.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new TestOutputLogger<MoonrakerSubscriptionService>(_output),
                httpClientFactory.Object,
                statusCacheWriter.Object);

            await _service.StartAsync(CancellationToken.None);

            PrinterStatusDto initial = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // Proves the wire-key fix: MoonrakerSubscriptionService.HandleMmuUpdate reads
            // "tool"/"gate" (not "active_tool"/"active_gate"), and gate_spool_id now round-trips
            // into MmuGateDto.SpoolId, through the real parser end to end.
            initial.MmuStatus.Should().NotBeNull();
            initial.MmuStatus!.MmuType.Should().Be("HappyHare");
            initial.MmuStatus.NumGates.Should().Be(4);
            initial.MmuStatus.Gates.Should().HaveCount(4);
            initial.MmuStatus.Gates[0].SpoolId.Should().Be(101);
            initial.MmuStatus.Gates[1].SpoolId.Should().Be(102);
            initial.MmuStatus.Gates[2].SpoolId.Should().Be(-1);
        }
        finally
        {
            using HttpResponseMessage resetMode = await _host.ControlClient.PostAsync(
                "/__emulator/printer/mmu",
                TestRequests.Json("""{"mode":"None"}"""));
            resetMode.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task ResetAsync_AfterMmuTopologyChange_ClearsStaleSubscriptionState()
    {
        await _host.ResetAsync();
        using HttpResponseMessage mmuMode = await _host.ControlClient.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"HappyHare"}"""));
        mmuMode.EnsureSuccessStatusCode();

        var printer = BuildPrinterPointingAtEmulator();
        var printersRepo = new Mock<IPrintersRepository>();
        printersRepo
            .Setup(r => r.GetByBackendWithToolheadsAsync(PrinterBackend.Moonraker, It.IsAny<CancellationToken>()))
            .ReturnsAsync([printer]);
        printersRepo
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.Printers).Returns(printersRepo.Object);

        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => unitOfWork.Object);
        services.AddScoped<IMoonrakerClient>(
            _ => new MoonrakerClient(new HttpClient(), NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()));
        await using ServiceProvider provider = services.BuildServiceProvider();

        var clientProxy = new Mock<IClientProxy>();
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hubContext = new Mock<IHubContext<PrinterHub>>();
        hubContext.SetupGet(h => h.Clients).Returns(hubClients.Object);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        var initialMmu = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearedMmu = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusCacheWriter = new Mock<IPrinterStatusCacheWriter>();
        statusCacheWriter
            .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
            .Callback<PrinterStatusDto, long?>((dto, _) =>
            {
                if (dto.MmuStatus is not null)
                {
                    initialMmu.TrySetResult(dto);
                }
                else if (initialMmu.Task.IsCompleted)
                {
                    clearedMmu.TrySetResult(dto);
                }
            });

        _service = new MoonrakerSubscriptionService(
            hubContext.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOutputLogger<MoonrakerSubscriptionService>(_output),
            httpClientFactory.Object,
            statusCacheWriter.Object);

        await _service.StartAsync(CancellationToken.None);
        (await initialMmu.Task.WaitAsync(TimeSpan.FromSeconds(15))).MmuStatus.Should().NotBeNull();

        await _host.ResetAsync();

        PrinterStatusDto reset = await clearedMmu.Task.WaitAsync(TimeSpan.FromSeconds(15));
        reset.MmuStatus.Should().BeNull();
    }

    [Fact]
    public async Task MmuAfcMode_ObjectSnapshot_IsParsedIntoMmuStatusDtoWithLaneNames()
    {
        await _host.ResetAsync();

        using HttpResponseMessage mmuMode = await _host.ControlClient.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"Afc"}"""));
        mmuMode.EnsureSuccessStatusCode();

        try
        {
            var printer = BuildPrinterPointingAtEmulator();

            var printersRepo = new Mock<IPrintersRepository>();
            printersRepo
                .Setup(r => r.GetByBackendWithToolheadsAsync(PrinterBackend.Moonraker, It.IsAny<CancellationToken>()))
                .ReturnsAsync([printer]);
            printersRepo
                .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(printer);

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.Printers).Returns(printersRepo.Object);

            var services = new ServiceCollection();
            services.AddScoped<IUnitOfWork>(_ => unitOfWork.Object);
            services.AddScoped<IMoonrakerClient>(
                _ => new MoonrakerClient(new HttpClient(), NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()));
            await using ServiceProvider provider = services.BuildServiceProvider();

            var clientProxy = new Mock<IClientProxy>();
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
            var hubContext = new Mock<IHubContext<PrinterHub>>();
            hubContext.SetupGet(h => h.Clients).Returns(hubClients.Object);

            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

            var firstUpdate = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusCacheWriter = new Mock<IPrinterStatusCacheWriter>();
            statusCacheWriter
                .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
                .Callback<PrinterStatusDto, long?>((dto, _) => firstUpdate.TrySetResult(dto));

            _service = new MoonrakerSubscriptionService(
                hubContext.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new TestOutputLogger<MoonrakerSubscriptionService>(_output),
                httpClientFactory.Object,
                statusCacheWriter.Object);

            await _service.StartAsync(CancellationToken.None);

            PrinterStatusDto initial = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // Proves the emulator's "AFC" + "AFC_stepper <lane>" object shapes round-trip
            // through the real MoonrakerSubscriptionService.HandleAfcUpdates parser, including
            // lane-name-to-gate-index resolution driven entirely by discovered object names.
            initial.MmuStatus.Should().NotBeNull();
            initial.MmuStatus!.MmuType.Should().Be("AFC");
            initial.MmuStatus.NumGates.Should().Be(4);
            initial.MmuStatus.Gates.Should().Contain(g => g.Name == "lane1");
        }
        finally
        {
            using HttpResponseMessage resetMode = await _host.ControlClient.PostAsync(
                "/__emulator/printer/mmu",
                TestRequests.Json("""{"mode":"None"}"""));
            resetMode.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task MmuQidiboxMode_ObjectSnapshotAndSeededDictionary_AreParsedIntoMmuStatusDto()
    {
        await _host.ResetAsync();

        using HttpResponseMessage mmuMode = await _host.ControlClient.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"Qidibox"}"""));
        mmuMode.EnsureSuccessStatusCode();

        try
        {
            var printer = BuildPrinterPointingAtEmulator();

            var printersRepo = new Mock<IPrintersRepository>();
            printersRepo
                .Setup(r => r.GetByBackendWithToolheadsAsync(PrinterBackend.Moonraker, It.IsAny<CancellationToken>()))
                .ReturnsAsync([printer]);
            printersRepo
                .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(printer);

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.Printers).Returns(printersRepo.Object);

            var services = new ServiceCollection();
            services.AddScoped<IUnitOfWork>(_ => unitOfWork.Object);
            services.AddScoped<IMoonrakerClient>(
                _ => new MoonrakerClient(new HttpClient(), NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()));
            await using ServiceProvider provider = services.BuildServiceProvider();

            var clientProxy = new Mock<IClientProxy>();
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
            var hubContext = new Mock<IHubContext<PrinterHub>>();
            hubContext.SetupGet(h => h.Clients).Returns(hubClients.Object);

            // Qidibox's dictionary fetch (FetchQidiboxDictionaryAsync) uses a raw HttpClient from
            // this factory — a real client (not mocked further) so it genuinely fetches
            // server/files/config/officiall_filas_list.cfg from the emulator over the network.
            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

            var firstUpdate = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusCacheWriter = new Mock<IPrinterStatusCacheWriter>();
            statusCacheWriter
                .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
                .Callback<PrinterStatusDto, long?>((dto, _) => firstUpdate.TrySetResult(dto));

            _service = new MoonrakerSubscriptionService(
                hubContext.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new TestOutputLogger<MoonrakerSubscriptionService>(_output),
                httpClientFactory.Object,
                statusCacheWriter.Object);

            await _service.StartAsync(CancellationToken.None);

            PrinterStatusDto initial = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // Proves the emulator's "box_stepper slotN" + "save_variables" object shapes, and the
            // seeded officiall_filas_list.cfg served through the new config-root download route,
            // round-trip through the real MoonrakerSubscriptionService.HandleQidiboxUpdatesAsync
            // parser (including its dictionary-code-to-name/color resolution) end to end.
            initial.MmuStatus.Should().NotBeNull();
            initial.MmuStatus!.MmuType.Should().Be("Qidibox");
            initial.MmuStatus.NumGates.Should().Be(4);
            initial.MmuStatus.ActiveTool.Should().Be(0);

            MmuGateDto slot0 = initial.MmuStatus.Gates.Should().ContainSingle(g => g.Name == "slot0").Subject;
            slot0.Material.Should().Be("PLA");
            slot0.Color.Should().Be("#FF0000");
            slot0.Status.Should().Be(1);

            MmuGateDto slot1 = initial.MmuStatus.Gates.Should().ContainSingle(g => g.Name == "slot1").Subject;
            slot1.Material.Should().Be("PETG");
            slot1.Color.Should().Be("#00A0FF");

            MmuGateDto slot2 = initial.MmuStatus.Gates.Should().ContainSingle(g => g.Name == "slot2").Subject;
            slot2.Status.Should().Be(0);

            MmuGateDto slot3 = initial.MmuStatus.Gates.Should().ContainSingle(g => g.Name == "slot3").Subject;
            slot3.Status.Should().Be(-1);
        }
        finally
        {
            using HttpResponseMessage resetMode = await _host.ControlClient.PostAsync(
                "/__emulator/printer/mmu",
                TestRequests.Json("""{"mode":"None"}"""));
            resetMode.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task MmuSnapmakerU1Mode_ObjectSnapshot_IsParsedIntoMmuStatusDtoWithPhysicalToolheadNames()
    {
        await _host.ResetAsync();

        using HttpResponseMessage mmuMode = await _host.ControlClient.PostAsync(
            "/__emulator/printer/mmu",
            TestRequests.Json("""{"mode":"SnapmakerU1"}"""));
        mmuMode.EnsureSuccessStatusCode();

        try
        {
            var printer = BuildPrinterPointingAtEmulator();

            var printersRepo = new Mock<IPrintersRepository>();
            printersRepo
                .Setup(r => r.GetByBackendWithToolheadsAsync(PrinterBackend.Moonraker, It.IsAny<CancellationToken>()))
                .ReturnsAsync([printer]);
            printersRepo
                .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(printer);

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.Printers).Returns(printersRepo.Object);

            var services = new ServiceCollection();
            services.AddScoped<IUnitOfWork>(_ => unitOfWork.Object);
            services.AddScoped<IMoonrakerClient>(
                _ => new MoonrakerClient(new HttpClient(), NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()));
            await using ServiceProvider provider = services.BuildServiceProvider();

            var clientProxy = new Mock<IClientProxy>();
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
            var hubContext = new Mock<IHubContext<PrinterHub>>();
            hubContext.SetupGet(h => h.Clients).Returns(hubClients.Object);

            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

            var firstUpdate = new TaskCompletionSource<PrinterStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusCacheWriter = new Mock<IPrinterStatusCacheWriter>();
            statusCacheWriter
                .Setup(w => w.UpdateStatus(It.IsAny<PrinterStatusDto>(), It.IsAny<long?>()))
                .Callback<PrinterStatusDto, long?>((dto, _) => firstUpdate.TrySetResult(dto));

            _service = new MoonrakerSubscriptionService(
                hubContext.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new TestOutputLogger<MoonrakerSubscriptionService>(_output),
                httpClientFactory.Object,
                statusCacheWriter.Object);

            await _service.StartAsync(CancellationToken.None);

            PrinterStatusDto initial = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // Proves the emulator's toolhead.extruder ("extruderN") + "print_task_config" arrays
            // round-trip through the real SnapmakerU1PrintTaskConfigParser and
            // HandleSnapmakerU1PrintTaskConfigUpdateAsync end to end, including the active
            // physical-toolhead index derived purely from toolhead.extruder's string value.
            initial.MmuStatus.Should().NotBeNull();
            initial.MmuStatus!.MmuType.Should().Be("SnapmakerU1");
            initial.MmuStatus.NumGates.Should().Be(4);
            initial.MmuStatus.ActiveTool.Should().Be(1);

            MmuGateDto t1 = initial.MmuStatus.Gates.Should().ContainSingle(g => g.Name == "T1").Subject;
            t1.Material.Should().Be("PLA");
            t1.Color.Should().Be("#FF0000");
            t1.Status.Should().Be(1);

            MmuGateDto t2 = initial.MmuStatus.Gates.Should().ContainSingle(g => g.Name == "T2").Subject;
            t2.Material.Should().Be("PETG");
            t2.Color.Should().Be("#00A0FF");
            t2.Status.Should().Be(1);

            MmuGateDto t3 = initial.MmuStatus.Gates.Should().ContainSingle(g => g.Name == "T3").Subject;
            t3.Status.Should().Be(0);
        }
        finally
        {
            using HttpResponseMessage resetMode = await _host.ControlClient.PostAsync(
                "/__emulator/printer/mmu",
                TestRequests.Json("""{"mode":"None"}"""));
            resetMode.EnsureSuccessStatusCode();
        }
    }

    private Printer BuildPrinterPointingAtEmulator()
    {
        var baseUri = new Uri(_host.BaseUrl);
        return new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Real subscription integration printer",
            ServerUrl = $"{baseUri.Scheme}://{baseUri.Host}",
            BackendPort = baseUri.Port,
            Backend = (int)PrinterBackend.Moonraker,
            IsEnabled = true,
            Toolheads =
            [
                new Toolhead
                {
                    Id = Guid.NewGuid(),
                    Name = "T0",
                    Index = 0,
                    ToolheadType = ToolheadType.Physical,
                },
            ],
        };
    }
}

/// <summary>Forwards log output to xUnit's <see cref="ITestOutputHelper"/> for debugging integration test failures.</summary>
internal sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
            if (exception is not null)
            {
                output.WriteLine(exception.ToString());
            }
        }
        catch (InvalidOperationException)
        {
            // Test runner already tore down the output sink; nothing more to log.
        }
    }
}
