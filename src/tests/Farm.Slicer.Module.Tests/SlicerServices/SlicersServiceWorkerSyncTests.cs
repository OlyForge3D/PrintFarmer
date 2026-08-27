using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.HostedServices;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.SlicerServices;

/// <summary>
/// Unit-level tests validating Phase 3 synchronization logic between SlicerService and Worker entity.
/// Avoids full WebApplicationFactory host to bypass OpenTelemetry MeterProvider requirements.
/// </summary>
public class SlicersServiceWorkerSyncTests
{
    private static SlicerDbContext CreateDb() => TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();

    private static Mock<IHubContext<SlicerHub>> CreateMockHub(out Mock<IClientProxy> clientProxy)
    {
        clientProxy = new Mock<IClientProxy>();
        _ = clientProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IHubClients> hubClients = new Mock<IHubClients>();
        _ = hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        Mock<IHubContext<SlicerHub>> hub = new Mock<IHubContext<SlicerHub>>();
        _ = hub.SetupGet(h => h.Clients).Returns(hubClients.Object);
        return hub;
    }

    private static SlicerServiceMetrics CreateMetrics() => new SlicerServiceMetrics();

    private static IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> CreateMockSlicerSettings()
    {
        Farm.Slicer.Module.Settings.SlicerSettings settings = new Farm.Slicer.Module.Settings.SlicerSettings
        {
            MaxConcurrentJobs = 10, // High enough not to interfere with tests
            MaxMemoryMb = 4096
        };
        Mock<IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings>> mock = new Mock<IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings>>();
        _ = mock.Setup(m => m.CurrentValue).Returns(settings);
        return mock.Object;
    }

    private static HttpClient CreateMockHttpClient()
    {
        HttpClientHandler handler = new HttpClientHandler
        {
            // Loopback-only test client (BaseAddress below is http://localhost); set explicitly
            // rather than defer CA5399, since it's cheap to fix at the two real call sites.
            CheckCertificateRevocationList = true,
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
    }

    private static Mock<IProcessProfileRepository> CreateMockProfileRepository()
    {
        Mock<IProcessProfileRepository> mock = new Mock<IProcessProfileRepository>(MockBehavior.Loose);
        return mock;
    }

    private static ILogger<SlicersService> CreateMockLogger()
    {
        return NullLogger<SlicersService>.Instance;
    }

    private static Mock<IFilamentProfileRepository> CreateMockFilamentProfileRepository()
    {
        return new Mock<IFilamentProfileRepository>(MockBehavior.Loose);
    }

    private static Mock<IMachineProfileRepository> CreateMockMachineProfileRepository()
    {
        return new Mock<IMachineProfileRepository>(MockBehavior.Loose);
    }

    private static Mock<IMachineModelProfileRepository> CreateMockMachineModelProfileRepository()
    {
        return new Mock<IMachineModelProfileRepository>(MockBehavior.Loose);
    }

    private static Mock<ICatalogService> CreateMockCatalogService()
    {
        Mock<ICatalogService> mock = new Mock<ICatalogService>(MockBehavior.Loose);
        // Return empty lists for catalog service; tests don't depend on actual catalog data for profile seeding
        _ = mock.Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<ManufacturerDto>(), null));
        _ = mock.Setup(c => c.GetModelsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<PrinterModelDto>(), null));
        return mock;
    }

    private static Mock<Farm.Infrastructure.Settings.ISettingsService> CreateMockSettingsService()
    {
        var mock = new Mock<Farm.Infrastructure.Settings.ISettingsService>(MockBehavior.Loose);
        // By default, lock operations return success (TryAcquireLockAsync returns true)
        _ = mock.Setup(s => s.TryAcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _ = mock.Setup(s => s.CompleteLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = mock.Setup(s => s.ClearLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static Mock<IPrinterModelAliasService> CreateMockAliasService()
    {
        var mock = new Mock<IPrinterModelAliasService>(MockBehavior.Loose);
        _ = mock.Setup(s => s.ResolveModelAliasAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid?)null);
        return mock;
    }

    [Fact(DisplayName = "RegisterAsync creates Worker with matching capabilities and slots")]
    public async Task RegisterAsync_Should_Create_Worker()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        ILogger<SlicersService> logger = CreateMockLogger();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger, settings);

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "sync-test-worker",
            SlicerType = 0,
            Version = "0.9.0",
            Host = "http://worker-host",
            MaxConcurrentJobs = 3,
            CapabilitiesJson = "[\"orcaslicer\",\"gcode-gen\"]"
        };

        (Guid id, string? apiKey) = await svc.RegisterAsync(dto, CancellationToken.None);

        _ = id.Should().NotBe(Guid.Empty);
        _ = apiKey.Should().NotBeNullOrWhiteSpace();

        // Verify slicer service exists
        SlicerService? slicer = await db.Set<SlicerService>().FindAsync(id);
        _ = slicer.Should().NotBeNull();
        _ = slicer!.Name.Should().Be("sync-test-worker");

        // Verify worker created via synchronization
        Worker? worker = await workerRepo.GetByServiceIdAsync(id.ToString());
        _ = worker.Should().NotBeNull();
        _ = worker!.Name.Should().Be("sync-test-worker");
        _ = worker.CapabilitiesJson.Should().Contain("orcaslicer");
        _ = worker.TotalSlots.Should().Be(3);
        _ = worker.FreeSlots.Should().Be(3);
        _ = worker.Status.Should().Be(WorkerStatus.Online);

        // Hub event broadcast attempted
        clientProxy.Verify(p => p.SendCoreAsync(
            It.Is<string>(s => s == SlicerHubEvents.SlicerRegistered),
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "RegisterAsync with a repeated InstanceId reuses the same service/worker row and rotates the key (issue #1528)")]
    public async Task RegisterAsync_WithSameInstanceId_UpsertsExistingRecord()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out _);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        ILogger<SlicersService> logger = CreateMockLogger();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger, settings);

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "redeploy-worker",
            SlicerType = 0,
            Version = "0.9.0",
            Host = "http://worker-host",
            MaxConcurrentJobs = 3,
            CapabilitiesJson = "[\"orcaslicer\"]",
            InstanceId = "orcaslicer-worker-1"
        };

        (Guid firstId, string firstApiKey) = await svc.RegisterAsync(dto, CancellationToken.None);

        Worker? firstWorker = await workerRepo.GetByServiceIdAsync(firstId.ToString());
        _ = firstWorker.Should().NotBeNull();
        Guid firstWorkerId = firstWorker!.Id;
        DateTime? firstHeartbeat = firstWorker.LastHeartbeat;
        _ = firstHeartbeat.Should().NotBeNull();

        // Ensure the timestamp comparison below is meaningful even on very fast test
        // hardware where both calls could otherwise land in the same UtcNow tick.
        await Task.Delay(15);

        // Simulate the worker cleanly shutting down (deregister) or the heartbeat monitor
        // detecting a stale heartbeat before the redeploy's registration arrives. Re-registering
        // over a still-Online worker is now rejected (issue #1860) — this Offline transition is
        // the legitimate precondition a real redeploy satisfies.
        firstWorker.Status = WorkerStatus.Offline;
        _ = await db.SaveChangesAsync();

        (Guid secondId, string secondApiKey) = await svc.RegisterAsync(dto, CancellationToken.None);

        _ = secondId.Should().Be(firstId, "re-registering under the same InstanceId must update the existing service, not create a new one");
        _ = secondApiKey.Should().NotBe(firstApiKey, "InstanceId must never be used to recover or reuse a prior credential");

        _ = db.Set<SlicerService>().Should().HaveCount(1);
        _ = db.Set<Worker>().Should().HaveCount(1, "worker count must stay at 1 across repeated redeploys of the same instance");

        Worker? worker = await workerRepo.GetByServiceIdAsync(firstId.ToString());
        _ = worker.Should().NotBeNull();
        _ = worker!.Id.Should().Be(firstWorkerId, "the same Worker row must be reused, not replaced, on redeploy");
        _ = worker.ApiKey.Should().Be(secondApiKey);
        _ = worker.LastHeartbeat.Should().BeAfter(firstHeartbeat!.Value, "the heartbeat must be refreshed on re-registration");
    }

    [Fact(DisplayName = "RegisterAsync rejects claiming a still-Online worker's InstanceId instead of revoking its credentials (issue #1860)")]
    public async Task RegisterAsync_WithInstanceIdOfOnlineWorker_RejectsWithoutMutatingCredentials()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out _);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        ILogger<SlicersService> logger = CreateMockLogger();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger, settings);

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "live-worker",
            SlicerType = 0,
            Version = "0.9.0",
            Host = "http://worker-host",
            MaxConcurrentJobs = 3,
            CapabilitiesJson = "[\"orcaslicer\"]",
            InstanceId = "orcaslicer-worker-1"
        };

        (Guid firstId, string firstApiKey) = await svc.RegisterAsync(dto, CancellationToken.None);

        Worker? firstWorker = await workerRepo.GetByServiceIdAsync(firstId.ToString());
        _ = firstWorker.Should().NotBeNull();
        _ = firstWorker!.Status.Should().Be(WorkerStatus.Online, "a freshly registered worker starts Online (it is still live, not squatted)");
        DateTime? firstHeartbeat = firstWorker.LastHeartbeat;

        // A holder of the shared registration key attempts to re-register the SAME InstanceId
        // while the worker is still Online — this is exactly the squatting attack from #1860:
        // no proof of possession of the live worker's own credentials is required to reach here.
        RegisterSlicerDto attackerDto = new RegisterSlicerDto
        {
            Name = "attacker-controlled-name",
            SlicerType = 0,
            Version = "9.9.9",
            Host = "http://attacker-host",
            MaxConcurrentJobs = 99,
            CapabilitiesJson = "[\"orcaslicer\"]",
            InstanceId = "orcaslicer-worker-1"
        };

        Func<Task> act = async () => await svc.RegisterAsync(attackerDto, CancellationToken.None);
        _ = await act.Should().ThrowAsync<SlicerInstanceIdConflictException>(
            "a live (non-Offline) worker's credentials must never be silently overwritten by a squatting registration");

        // No credentials, status, or fields were mutated: the guard must reject before any save.
        _ = db.Set<SlicerService>().Should().HaveCount(1);
        _ = db.Set<Worker>().Should().HaveCount(1);

        SlicerService? unchangedService = await db.Set<SlicerService>().FindAsync(firstId);
        _ = unchangedService.Should().NotBeNull();
        _ = unchangedService!.ApiKey.Should().Be(firstApiKey, "the attacker's registration must not rotate the live worker's credentials");
        _ = unchangedService.Name.Should().Be("live-worker", "the attacker's fields must not overwrite the live worker's own registration");

        Worker? unchangedWorker = await workerRepo.GetByServiceIdAsync(firstId.ToString());
        _ = unchangedWorker.Should().NotBeNull();
        _ = unchangedWorker!.ApiKey.Should().Be(firstApiKey);
        _ = unchangedWorker.Status.Should().Be(WorkerStatus.Online, "the worker must remain Online, unaffected by the rejected squatting attempt");
        _ = unchangedWorker.LastHeartbeat.Should().Be(firstHeartbeat, "the heartbeat must not be touched by a rejected registration");
    }

    [Fact(DisplayName = "RegisterAsync reclaims a non-Offline worker whose heartbeat has actually gone stale (issue #1860 follow-up)")]
    public async Task RegisterAsync_WithStaleHeartbeatOnNonOfflineWorker_AllowsLegitimateRedeploy()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out _);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        ILogger<SlicersService> logger = CreateMockLogger();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger, settings);

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "flaky-worker",
            SlicerType = 0,
            Version = "0.9.0",
            Host = "http://worker-host",
            MaxConcurrentJobs = 3,
            CapabilitiesJson = "[\"orcaslicer\"]",
            InstanceId = "orcaslicer-worker-7"
        };

        (Guid firstId, string firstApiKey) = await svc.RegisterAsync(dto, CancellationToken.None);

        Worker? firstWorker = await workerRepo.GetByServiceIdAsync(firstId.ToString());
        _ = firstWorker.Should().NotBeNull();
        Guid firstWorkerId = firstWorker!.Id;

        // Simulate the worker crashing mid-job: it never reaches Offline because
        // WorkerHealthMonitorService's stale sweep only reclassifies stale *Online* workers
        // (see EfWorkerRepository.GetStaleWorkersAsync) — a worker that dies while Busy is
        // never swept by that monitor. Without gating on heartbeat freshness, this worker
        // would stay non-Offline and un-reclaimable by a legitimate redeploy until the much
        // longer stale-worker cleanup job runs (default up to 24h).
        firstWorker.Status = WorkerStatus.Busy;
        firstWorker.LastHeartbeat = DateTime.UtcNow.AddSeconds(-(WorkerStatus.LiveHeartbeatTimeoutSeconds + 30));
        _ = await db.SaveChangesAsync();

        (Guid secondId, string secondApiKey) = await svc.RegisterAsync(dto, CancellationToken.None);

        _ = secondId.Should().Be(firstId, "redeploy of a genuinely stale worker must update the existing service, not be rejected");
        _ = secondApiKey.Should().NotBe(firstApiKey, "redeploy must always issue fresh credentials");

        Worker? worker = await workerRepo.GetByServiceIdAsync(firstId.ToString());
        _ = worker.Should().NotBeNull();
        _ = worker!.Id.Should().Be(firstWorkerId, "the same Worker row must be reused, not replaced, on redeploy");
        _ = worker.ApiKey.Should().Be(secondApiKey);
        _ = worker.Status.Should().Be(WorkerStatus.Online, "the reclaimed worker must be reported Online after a successful redeploy");
    }

    [Fact(DisplayName = "RegisterAsync sanitizes a CRLF-injected InstanceId before logging it (cs/log-forging)")]
    public async Task RegisterAsync_WithCrlfInInstanceId_SanitizesLoggedValue()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out _);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        Mock<ILogger<SlicersService>> loggerMock = new Mock<ILogger<SlicersService>>();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, loggerMock.Object, settings);

        const string maliciousInstanceId = "orcaslicer-worker-1\r\nFAKE LOG LINE: admin logged in";
        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "crlf-worker",
            SlicerType = 0,
            Version = "0.9.0",
            Host = "http://worker-host",
            MaxConcurrentJobs = 3,
            CapabilitiesJson = "[\"orcaslicer\"]",
            InstanceId = maliciousInstanceId
        };

        // First call inserts the row; the second call hits the "Re-registering" log path
        // (svc is not null) that interpolates InstanceId directly. Mark the worker Offline in
        // between so the second call represents a legitimate redeploy rather than squatting a
        // still-live instance (issue #1860).
        _ = await svc.RegisterAsync(dto, CancellationToken.None);
        Worker? crlfWorker = await workerRepo.GetByServiceIdAsync((await slicerRepo.GetByInstanceIdAsync(maliciousInstanceId, CancellationToken.None))!.Id.ToString());
        crlfWorker!.Status = WorkerStatus.Offline;
        _ = await db.SaveChangesAsync();
        _ = await svc.RegisterAsync(dto, CancellationToken.None);

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("Re-registering slicer service", StringComparison.Ordinal) &&
                    v.ToString()!.Contains("orcaslicer-worker-1\\r\\nFAKE LOG LINE", StringComparison.Ordinal) &&
                    !v.ToString()!.Contains("\r\nFAKE", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact(DisplayName = "RegisterAsync without an InstanceId always creates a new service/worker (scaled replicas stay distinct)")]
    public async Task RegisterAsync_WithoutInstanceId_AlwaysCreatesNewRecord()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out _);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        ILogger<SlicersService> logger = CreateMockLogger();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger, settings);

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "scaled-worker",
            SlicerType = 0,
            Version = "0.9.0",
            Host = "http://worker-host",
            MaxConcurrentJobs = 3,
            CapabilitiesJson = "[\"orcaslicer\"]"
        };

        (Guid firstId, _) = await svc.RegisterAsync(dto, CancellationToken.None);
        (Guid secondId, _) = await svc.RegisterAsync(dto, CancellationToken.None);

        _ = secondId.Should().NotBe(firstId, "without a shared InstanceId each registration must remain a distinct worker (e.g. scaled replicas)");
        _ = db.Set<SlicerService>().Should().HaveCount(2);
        _ = db.Set<Worker>().Should().HaveCount(2);
    }

    [Fact(DisplayName = "SlicerService.InstanceId has a unique database constraint, closing the upsert race window (issue #1528)")]
    public async Task SlicerServiceConfiguration_RejectsDuplicateInstanceIdAtTheDatabaseLevel()
    {
        // RegisterAsync's GetByInstanceIdAsync-then-insert is not atomic: two concurrent
        // registrations for the same stable InstanceId could both read "no existing row"
        // before either commits. Without a database-level unique constraint, both inserts
        // would succeed and violate the "worker count stays at 1" acceptance criterion.
        // This proves the constraint declared in SlicerServiceConfiguration is actually
        // enforced by the database, not just declared in the EF model (UpsertServiceAndWorkerAsync
        // relies on this to turn the losing insert into a catchable DbUpdateException it can
        // retry as an update instead of a duplicate).
        using SlicerDbContext db = CreateDb();

        db.Set<SlicerService>().Add(new SlicerService
        {
            Id = Guid.NewGuid(),
            Name = "first",
            InstanceId = "orcaslicer-worker-1",
            Status = "Online",
        });
        _ = await db.SaveChangesAsync();

        db.Set<SlicerService>().Add(new SlicerService
        {
            Id = Guid.NewGuid(),
            Name = "second",
            InstanceId = "orcaslicer-worker-1", // same InstanceId as an existing row
            Status = "Online",
        });

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact(DisplayName = "RegisterAsync fails closed without persisting an orphaned service")]
    public async Task RegisterAsync_WhenWorkerSynchronizationFails_DoesNotPersistService()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        var workerRepo = new Mock<IWorkerRepository>(MockBehavior.Strict);
        _ = workerRepo
            .Setup(repository => repository.GetByServiceIdAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("worker synchronization failed"));
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out _);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        using HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        var service = new SlicersService(
            slicerRepo,
            workerRepo.Object,
            profileRepo.Object,
            filamentProfileRepo.Object,
            machineProfileRepo.Object,
            machineModelProfileRepo.Object,
            catalogService.Object,
            aliasService.Object,
            settingsService.Object,
            mockHub.Object,
            metrics,
            httpClient,
            CreateMockLogger(),
            settings);
        var request = new RegisterSlicerDto
        {
            Name = "orphan-test-worker",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://worker.internal",
            MaxConcurrentJobs = 1,
        };

        Func<Task> register = async () =>
            await service.RegisterAsync(request, CancellationToken.None);

        _ = await register.Should().ThrowAsync<InvalidOperationException>();
        _ = db.Set<SlicerService>().Should().BeEmpty();
    }

    [Fact(DisplayName = "HeartbeatAsync updates Worker FreeSlots, ActiveJobs, Status")]
    public async Task HeartbeatAsync_Should_Update_Worker()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        ILogger<SlicersService> logger = CreateMockLogger();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger, settings);

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "sync-heartbeat-worker",
            SlicerType = 0,
            Version = "1.0.0",
            Host = "http://worker-heartbeat",
            MaxConcurrentJobs = 4,
            CapabilitiesJson = "[\"orcaslicer\"]"
        };
        (Guid id, _) = await svc.RegisterAsync(dto, CancellationToken.None);

        // Simulate heartbeat with fewer free slots (2 of 4 used)
        HeartbeatDto hb = new HeartbeatDto
        {
            Status = "Busy",
            FreeSlots = 2
        };
        bool ok = await svc.HeartbeatAsync(id, hb, CancellationToken.None);
        _ = ok.Should().BeTrue();

        Worker? worker = await workerRepo.GetByServiceIdAsync(id.ToString());
        _ = worker.Should().NotBeNull();
        _ = worker!.FreeSlots.Should().Be(2);
        _ = worker.ActiveJobs.Should().Be(2); // TotalSlots(4) - FreeSlots(2)
        _ = worker.Status.Should().Be(WorkerStatus.Busy);

        clientProxy.Verify(p => p.SendCoreAsync(
            It.Is<string>(s => s == SlicerHubEvents.SlicerHeartbeat),
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "DeregisterAsync revokes and disables Worker credentials")]
    public async Task DeregisterAsync_Should_Revoke_Worker_Credentials()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? clientProxy);
        SlicerServiceMetrics metrics = CreateMetrics();
        IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings> settings = CreateMockSlicerSettings();
        HttpClient httpClient = CreateMockHttpClient();
        Mock<IProcessProfileRepository> profileRepo = CreateMockProfileRepository();
        Mock<IFilamentProfileRepository> filamentProfileRepo = CreateMockFilamentProfileRepository();
        ILogger<SlicersService> logger = CreateMockLogger();
        Mock<ICatalogService> catalogService = CreateMockCatalogService();
        Mock<IPrinterModelAliasService> aliasService = CreateMockAliasService();
        Mock<Farm.Infrastructure.Settings.ISettingsService> settingsService = CreateMockSettingsService();
        Mock<IMachineProfileRepository> machineProfileRepo = CreateMockMachineProfileRepository();
        Mock<IMachineModelProfileRepository> machineModelProfileRepo = CreateMockMachineModelProfileRepository();
        SlicersService svc = new SlicersService(slicerRepo, workerRepo, profileRepo.Object, filamentProfileRepo.Object, machineProfileRepo.Object, machineModelProfileRepo.Object, catalogService.Object, aliasService.Object, settingsService.Object, mockHub.Object, metrics, httpClient, logger, settings);

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "sync-deregister-worker",
            SlicerType = 0,
            Version = "1.0.0",
            Host = "http://worker-dereg",
            MaxConcurrentJobs = 1
        };
        (Guid id, _) = await svc.RegisterAsync(dto, CancellationToken.None);

        bool ok = await svc.DeregisterAsync(id, retainForReregistration: false, CancellationToken.None);
        _ = ok.Should().BeTrue();

        Worker? worker = await workerRepo.GetByServiceIdAsync(id.ToString());
        _ = worker.Should().NotBeNull();
        _ = worker!.Status.Should().Be(WorkerStatus.Offline);
        _ = worker.OfflineAt.Should().NotBeNull();
        _ = worker.IsDisabled.Should().BeTrue();
        _ = worker.DisabledReason.Should().Be("Slicer service deregistered");
        _ = worker.ApiKey.Should().BeNull();

        clientProxy.Verify(p => p.SendCoreAsync(
            It.Is<string>(s => s == SlicerHubEvents.SlicerDeregistered),
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Capacity gauges exclude offline and disabled workers")]
    public async Task CapacityGauges_Should_Exclude_Offline_And_Disabled_Workers()
    {
        using SlicerDbContext db = CreateDb();
        EfWorkerRepository workerRepo = new(db);
        Worker[] workers =
        [
            CreateWorker("online", WorkerStatus.Online, totalSlots: 4, activeJobs: 1),
            CreateWorker("busy", WorkerStatus.Busy, totalSlots: 2, activeJobs: 2),
            CreateWorker("draining", WorkerStatus.Draining, totalSlots: 3, activeJobs: 1),
            CreateWorker("offline", WorkerStatus.Offline, totalSlots: 100, activeJobs: 50),
            CreateWorker("disabled", WorkerStatus.Online, totalSlots: 100, activeJobs: 50, isDisabled: true),
        ];
        await db.Set<Worker>().AddRangeAsync(workers);
        await db.SaveChangesAsync();

        using SlicerServiceMetrics metrics = CreateMetrics();

        // The gauge callbacks read a snapshot published by SlicerCapacityMetricsRefreshService
        // rather than a delegate bound to a scoped service (see #1676). Drive one refresh
        // cycle against a DI container that resolves IWorkerRepository from the test db.
        ServiceCollection services = new();
        _ = services.AddSingleton<IWorkerRepository>(workerRepo);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        SlicerCapacityMetricsRefreshService refresher = new(
            serviceProvider,
            metrics,
            NullLogger<SlicerCapacityMetricsRefreshService>.Instance);

        await refresher.RefreshOnceAsync();

        Dictionary<string, int> observed = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument, metrics.ServiceTotalCapacity) ||
                ReferenceEquals(instrument, metrics.ServiceAvailableCapacity) ||
                ReferenceEquals(instrument, metrics.ServiceActiveJobs))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
            observed[instrument.Name] = measurement);
        listener.Start();

        listener.RecordObservableInstruments();

        _ = observed["printfarmer.slicer.service_total_capacity"].Should().Be(9);
        _ = observed["printfarmer.slicer.service_available_capacity"].Should().Be(3);
        _ = observed["printfarmer.slicer.service_active_jobs"].Should().Be(4);
    }

    [Fact(DisplayName = "Stale worker cleanup deletes expired identities by default")]
    public void StaleWorkerCleanup_Should_Default_To_AutoDelete()
    {
        _ = new StaleWorkerCleanupSettings().AutoDelete.Should().BeTrue();
    }

    [Fact(DisplayName = "Stale worker cleanup deletes the paired service identity")]
    public async Task StaleWorkerCleanup_Should_Delete_Paired_Service_Identity()
    {
        using SlicerDbContext db = CreateDb();
        Guid serviceId = Guid.NewGuid();
        DateTime staleHeartbeat = DateTime.UtcNow.AddHours(-2);
        _ = await db.Set<SlicerService>().AddAsync(new SlicerService
        {
            Id = serviceId,
            Name = "stale-service",
            ApiKey = "stale-key",
            LastSeen = staleHeartbeat,
        });
        _ = await db.Set<Worker>().AddAsync(new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
            Name = "stale-worker",
            EndpointUrl = "http://stale-worker.internal",
            Status = WorkerStatus.Offline,
            ApiKey = "stale-key",
            LastHeartbeat = staleHeartbeat,
            RegisteredAt = staleHeartbeat,
            CreatedAt = staleHeartbeat,
            UpdatedAt = staleHeartbeat,
        });
        await db.SaveChangesAsync();

        ServiceCollection services = new();
        _ = services.AddSingleton<IWorkerRepository>(new EfWorkerRepository(db));
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        StaleWorkerCleanupSettings settings = new()
        {
            AutoDelete = true,
            StaleAfterMinutes = 60,
        };
        var settingsMonitor = new Mock<IOptionsMonitor<StaleWorkerCleanupSettings>>();
        _ = settingsMonitor.SetupGet(monitor => monitor.CurrentValue).Returns(settings);
        StaleWorkerCleanupHostedService cleanup = new(
            serviceProvider,
            NullLogger<StaleWorkerCleanupHostedService>.Instance,
            settingsMonitor.Object);

        await cleanup.CleanupStaleWorkersAsync(settings);

        db.ChangeTracker.Clear();
        _ = db.Set<Worker>().Should().BeEmpty();
        _ = db.Set<SlicerService>().Should().BeEmpty();
    }

    [Fact(DisplayName = "Stale worker cleanup retains workers an administrator disabled")]
    public async Task StaleWorkerCleanup_Should_Retain_Administratively_Disabled_Workers()
    {
        using SlicerDbContext db = CreateDb();
        Guid serviceId = Guid.NewGuid();
        DateTime staleHeartbeat = DateTime.UtcNow.AddHours(-2);
        _ = await db.Set<SlicerService>().AddAsync(new SlicerService
        {
            Id = serviceId,
            Name = "banned-service",
            ApiKey = null,
            LastSeen = staleHeartbeat,
        });
        _ = await db.Set<Worker>().AddAsync(new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
            Name = "banned-worker",
            EndpointUrl = "http://banned-worker.internal",
            Status = WorkerStatus.Offline,
            IsDisabled = true,
            DisabledReason = "Banned by administrator: producing scrap",
            DisableSource = WorkerDisableSource.Administrator,
            LastHeartbeat = staleHeartbeat,
            RegisteredAt = staleHeartbeat,
            CreatedAt = staleHeartbeat,
            UpdatedAt = staleHeartbeat,
        });
        await db.SaveChangesAsync();

        ServiceCollection services = new();
        _ = services.AddSingleton<IWorkerRepository>(new EfWorkerRepository(db));
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        StaleWorkerCleanupSettings settings = new()
        {
            AutoDelete = true,
            StaleAfterMinutes = 60,
        };
        var settingsMonitor = new Mock<IOptionsMonitor<StaleWorkerCleanupSettings>>();
        _ = settingsMonitor.SetupGet(monitor => monitor.CurrentValue).Returns(settings);
        StaleWorkerCleanupHostedService cleanup = new(
            serviceProvider,
            NullLogger<StaleWorkerCleanupHostedService>.Instance,
            settingsMonitor.Object);

        await cleanup.CleanupStaleWorkersAsync(settings);

        // The ban lives in this row. Deleting it would let the worker return as brand new and
        // come back enabled, so a banned worker could outlast its ban simply by staying offline.
        db.ChangeTracker.Clear();
        _ = db.Set<Worker>().Should().ContainSingle();
        _ = db.Set<SlicerService>().Should().ContainSingle();
    }

    [Fact(DisplayName = "Stale worker cleanup still deletes workers the circuit breaker disabled")]
    public async Task StaleWorkerCleanup_Should_Delete_CircuitBreaker_Disabled_Workers()
    {
        using SlicerDbContext db = CreateDb();
        Guid serviceId = Guid.NewGuid();
        DateTime staleHeartbeat = DateTime.UtcNow.AddHours(-2);
        _ = await db.Set<SlicerService>().AddAsync(new SlicerService
        {
            Id = serviceId,
            Name = "tripped-service",
            ApiKey = null,
            LastSeen = staleHeartbeat,
        });
        _ = await db.Set<Worker>().AddAsync(new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
            Name = "tripped-worker",
            EndpointUrl = "http://tripped-worker.internal",
            Status = WorkerStatus.Offline,
            IsDisabled = true,
            DisabledReason = WorkerDisableReasons.CircuitBreaker(5, 60),
            DisableSource = WorkerDisableSource.CircuitBreaker,
            LastHeartbeat = staleHeartbeat,
            RegisteredAt = staleHeartbeat,
            CreatedAt = staleHeartbeat,
            UpdatedAt = staleHeartbeat,
        });
        await db.SaveChangesAsync();

        ServiceCollection services = new();
        _ = services.AddSingleton<IWorkerRepository>(new EfWorkerRepository(db));
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        StaleWorkerCleanupSettings settings = new()
        {
            AutoDelete = true,
            StaleAfterMinutes = 60,
        };
        var settingsMonitor = new Mock<IOptionsMonitor<StaleWorkerCleanupSettings>>();
        _ = settingsMonitor.SetupGet(monitor => monitor.CurrentValue).Returns(settings);
        StaleWorkerCleanupHostedService cleanup = new(
            serviceProvider,
            NullLogger<StaleWorkerCleanupHostedService>.Instance,
            settingsMonitor.Object);

        await cleanup.CleanupStaleWorkersAsync(settings);

        // The circuit breaker is an automatic disabler, so its rows must still be swept. Exempting
        // them would retain every worker the breaker ever tripped for good — unbounded growth in
        // exchange for protecting a sanction nobody imposed.
        db.ChangeTracker.Clear();
        _ = db.Set<Worker>().Should().BeEmpty();
        _ = db.Set<SlicerService>().Should().BeEmpty();
    }

    [Fact(DisplayName = "Stale worker cleanup keeps a worker banned after its snapshot was taken")]
    public async Task StaleWorkerCleanup_Should_Retain_A_Worker_Banned_After_The_Sweep_Read_It()
    {
        using SlicerDbContext db = CreateDb();
        Guid serviceId = Guid.NewGuid();
        DateTime staleHeartbeat = DateTime.UtcNow.AddHours(-2);
        _ = await db.Set<SlicerService>().AddAsync(new SlicerService
        {
            Id = serviceId,
            Name = "raced-service",
            ApiKey = null,
            LastSeen = staleHeartbeat,
        });
        _ = await db.Set<Worker>().AddAsync(new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
            Name = "raced-worker",
            EndpointUrl = "http://raced-worker.internal",
            Status = WorkerStatus.Offline,
            IsDisabled = false,
            LastHeartbeat = staleHeartbeat,
            RegisteredAt = staleHeartbeat,
            CreatedAt = staleHeartbeat,
            UpdatedAt = staleHeartbeat,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        EfWorkerRepository repository = new(db);

        // The sweep selects its candidates from an AsNoTracking snapshot, so this detached copy is
        // exactly what it carries into the delete loop. At this point the worker is not banned.
        IReadOnlyList<Worker> snapshot = await repository.GetAllAsync(int.MaxValue, 0);
        Worker staleSnapshot = snapshot.Should().ContainSingle().Subject;
        _ = staleSnapshot.IsDisabled.Should().BeFalse();

        // An administrator bans it while the sweep is still working through its list.
        await repository.DisableWorkerAsync(
            staleSnapshot.Id,
            "Banned by administrator: racing the sweep",
            WorkerDisableSource.Administrator);
        await repository.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The sweep proceeds on the stale snapshot, which still reports the worker as enabled.
        bool deleted = await repository.DeleteIfNotAdministrativelyDisabledAsync(staleSnapshot.Id);

        // An unconditional delete would erase the ban along with the row, and the worker could
        // return, register as brand new and come back enabled — the sanction laundered by a
        // background job. The exemption is re-checked by the database inside the delete instead.
        _ = deleted.Should().BeFalse();

        db.ChangeTracker.Clear();
        _ = db.Set<Worker>().Should().ContainSingle();
        _ = db.Set<SlicerService>().Should().ContainSingle();
    }

    [Fact(DisplayName = "Stale worker cleanup delete enlists in the caller's transaction")]
    public async Task StaleWorkerCleanup_Delete_Should_Enlist_In_The_Callers_Transaction()
    {
        using SlicerDbContext db = CreateDb();
        Guid serviceId = Guid.NewGuid();
        DateTime staleHeartbeat = DateTime.UtcNow.AddHours(-2);
        _ = await db.Set<SlicerService>().AddAsync(new SlicerService
        {
            Id = serviceId,
            Name = "atomic-service",
            ApiKey = null,
            LastSeen = staleHeartbeat,
        });
        _ = await db.Set<Worker>().AddAsync(new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
            Name = "atomic-worker",
            EndpointUrl = "http://atomic-worker.internal",
            Status = WorkerStatus.Offline,
            IsDisabled = false,
            LastHeartbeat = staleHeartbeat,
            RegisteredAt = staleHeartbeat,
            CreatedAt = staleHeartbeat,
            UpdatedAt = staleHeartbeat,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        EfWorkerRepository repository = new(db);
        Guid workerId = db.Set<Worker>().AsNoTracking().Single().Id;
        db.ChangeTracker.Clear();

        // The worker row and its paired service row must go together. Two independent statements
        // would orphan the service if the second never ran, and no later sweep could collect it:
        // cleanup enumerates Workers, so a service with no worker is invisible to it. Rolling back
        // an enclosing transaction proves both deletes belong to one atomic unit — and that the
        // method enlists in the caller's transaction instead of opening and committing its own.
        await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync())
        {
            bool deleted = await repository.DeleteIfNotAdministrativelyDisabledAsync(workerId);
            _ = deleted.Should().BeTrue();

            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        _ = db.Set<Worker>().Should().ContainSingle();
        _ = db.Set<SlicerService>().Should().ContainSingle();
    }

    [Fact(DisplayName = "Stale worker cleanup does not orphan the service when its delete fails")]
    public async Task StaleWorkerCleanup_Delete_Should_Roll_Back_The_Worker_When_The_Service_Delete_Fails()
    {
        SqliteConnection connection = new("DataSource=:memory:");
        await connection.OpenAsync();

        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailServiceDeleteInterceptor())
            .Options;

        using SlicerDbContext db = new(options);
        _ = db.Database.EnsureCreated();

        Guid serviceId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        DateTime staleHeartbeat = DateTime.UtcNow.AddHours(-2);
        _ = await db.Set<SlicerService>().AddAsync(new SlicerService
        {
            Id = serviceId,
            Name = "orphan-service",
            ApiKey = null,
            LastSeen = staleHeartbeat,
        });
        _ = await db.Set<Worker>().AddAsync(new Worker
        {
            Id = workerId,
            ServiceId = serviceId.ToString(),
            Name = "orphan-worker",
            EndpointUrl = "http://orphan-worker.internal",
            Status = WorkerStatus.Offline,
            IsDisabled = false,
            LastHeartbeat = staleHeartbeat,
            RegisteredAt = staleHeartbeat,
            CreatedAt = staleHeartbeat,
            UpdatedAt = staleHeartbeat,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        EfWorkerRepository repository = new(db);

        Func<Task> delete = async () => await repository.DeleteIfNotAdministrativelyDisabledAsync(workerId);
        _ = await delete.Should().ThrowAsync<InvalidOperationException>();

        // Two independent statements would have committed the worker delete before the service
        // delete failed, orphaning the service for good: the sweep enumerates Workers, so a
        // service with no worker is invisible to it and no later pass can collect it. In one
        // transaction the pair either both go or both stay.
        db.ChangeTracker.Clear();
        _ = db.Set<Worker>().Should().ContainSingle("the worker delete must roll back with the service delete");
        _ = db.Set<SlicerService>().Should().ContainSingle();

        await connection.DisposeAsync();
    }

    /// <summary>
    /// Fails the paired service delete so the atomicity of the two statements can be observed.
    /// </summary>
    private sealed class FailServiceDeleteInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfDeletingAService(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDeletingAService(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfDeletingAService(DbCommand command)
        {
            if (command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("SlicerServices", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated failure deleting the paired service row.");
            }
        }
    }

    [Fact(DisplayName = "Registration leaves a worker disabled when its save fails")]
    public async Task RegisterAsync_Should_Leave_Worker_Disabled_When_The_Save_Fails()
    {
        using SlicerDbContext db = CreateDb();
        EfSlicersRepository slicerRepo = new EfSlicersRepository(db);
        EfWorkerRepository workerRepo = new EfWorkerRepository(db);
        Mock<IHubContext<SlicerHub>> mockHub = CreateMockHub(out Mock<IClientProxy>? _);
        SlicersService svc = new SlicersService(
            slicerRepo,
            workerRepo,
            CreateMockProfileRepository().Object,
            CreateMockFilamentProfileRepository().Object,
            CreateMockMachineProfileRepository().Object,
            CreateMockMachineModelProfileRepository().Object,
            CreateMockCatalogService().Object,
            CreateMockAliasService().Object,
            CreateMockSettingsService().Object,
            mockHub.Object,
            CreateMetrics(),
            CreateMockHttpClient(),
            CreateMockLogger(),
            CreateMockSlicerSettings());

        RegisterSlicerDto dto = new RegisterSlicerDto
        {
            Name = "breaker-tripped-worker",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://breaker-worker.internal",
            MaxConcurrentJobs = 3,
            InstanceId = "orcaslicer-worker-breaker-save-failure",
        };

        (Guid id, string _) = await svc.RegisterAsync(dto, CancellationToken.None);

        // The circuit breaker takes a failing worker out of rotation but leaves it Online, so the
        // only thing keeping it out of dispatch is IsDisabled.
        await workerRepo.DisableWorkerAsync(
            db.Set<Worker>().Single(w => w.ServiceId == id.ToString()).Id,
            WorkerDisableReasons.CircuitBreaker(5, 60),
            WorkerDisableSource.CircuitBreaker);

        // The worker then restarts. #1863 only permits reclaiming a non-live incumbent, so age
        // the heartbeat past the liveness window; otherwise the re-registration below is rejected
        // as squatting and never reaches the save-failure path this test is about.
        db.Set<Worker>().Single(w => w.ServiceId == id.ToString()).LastHeartbeat =
            DateTime.UtcNow.AddSeconds(-(WorkerStatus.LiveHeartbeatTimeoutSeconds + 30));
        await workerRepo.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Re-register through a repository whose save always fails.
        SlicersService failing = new SlicersService(
            new SaveFailingSlicersRepository(slicerRepo),
            workerRepo,
            CreateMockProfileRepository().Object,
            CreateMockFilamentProfileRepository().Object,
            CreateMockMachineProfileRepository().Object,
            CreateMockMachineModelProfileRepository().Object,
            CreateMockCatalogService().Object,
            CreateMockAliasService().Object,
            CreateMockSettingsService().Object,
            mockHub.Object,
            CreateMetrics(),
            CreateMockHttpClient(),
            CreateMockLogger(),
            CreateMockSlicerSettings());

        Func<Task> register = async () => await failing.RegisterAsync(dto, CancellationToken.None);
        _ = await register.Should().ThrowAsync<DbUpdateException>();

        // Clearing the disable commits on its own, so doing it before the registration is durable
        // would hand a worker the breaker had removed straight back to the dispatcher — with stale
        // credentials — and leave it there. Every failure direction must leave it disabled.
        db.ChangeTracker.Clear();
        Worker afterFailure = db.Set<Worker>().Single(w => w.ServiceId == id.ToString());
        _ = afterFailure.IsDisabled.Should().BeTrue(
            "a registration that did not persist must not re-enable the worker");
        _ = afterFailure.DisableSource.Should().Be(WorkerDisableSource.CircuitBreaker);

        IReadOnlyList<Worker> dispatchable = await workerRepo.GetAvailableWorkersAsync();
        _ = dispatchable.Should().BeEmpty("the breaker's disable is all that keeps this worker out of dispatch");
    }

    /// <summary>
    /// Wraps a real repository and fails every save, to exercise the ordering guarantee that a
    /// worker is only re-enabled once the registration justifying it is durable.
    /// </summary>
    private sealed class SaveFailingSlicersRepository(ISlicersRepository inner) : ISlicersRepository
    {
        public Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);

        public Task AddAsync(SlicerService svc, CancellationToken ct) => inner.AddAsync(svc, ct);

        public Task<SlicerService?> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

        public Task<SlicerService?> GetByInstanceIdAsync(string instanceId, CancellationToken ct)
            => inner.GetByInstanceIdAsync(instanceId, ct);

        public Task RemoveAsync(SlicerService svc, CancellationToken ct) => inner.RemoveAsync(svc, ct);

        public Task SaveChangesAsync(CancellationToken ct)
            => throw new DbUpdateException("Simulated save failure.");

        public void ClearTracking() => inner.ClearTracking();
    }

    private static Worker CreateWorker(
        string name,
        string status,
        int totalSlots,
        int activeJobs,
        bool isDisabled = false)
    {
        DateTime now = DateTime.UtcNow;
        return new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = Guid.NewGuid().ToString(),
            Name = name,
            EndpointUrl = $"http://{name}.internal",
            Status = status,
            TotalSlots = totalSlots,
            ActiveJobs = activeJobs,
            LastHeartbeat = now,
            RegisteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            ApiKey = $"key-{name}",
            IsDisabled = isDisabled,
        };
    }
}
