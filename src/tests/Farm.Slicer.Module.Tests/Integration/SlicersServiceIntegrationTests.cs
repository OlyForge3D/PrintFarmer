using System;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;


namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Integration tests for SlicersService
/// Tests slicer registration, deregistration, heartbeat, API key rotation, and Worker synchronization
/// Fast executing (~2 seconds for 20 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class SlicersServiceIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public SlicersServiceIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    #region RegisterAsync Tests

    [Fact]
    public async Task RegisterAsync_WithValidDto_CreatesSlicerService()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-test-1",
            SlicerType = 1, // OrcaSlicer
            Version = "2.3.1",
            Host = "http://localhost:8080",
            UiManifestUrl = "http://localhost:8080/manifest.json",
            CapabilitiesJson = "{\"features\": [\"slicing\", \"profiling\"]}",
            MaxConcurrentJobs = 10,
            Tags = "test"
        };

        // Act
        (Guid id, string? apiKey) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert
        id.Should().NotBeEmpty();
        apiKey.Should().NotBeNullOrEmpty();
        apiKey.Should().NotContain("="); // Base64 padding removed

        SlicerService? createdSlicer = await context.Set<SlicerService>().FindAsync(id);
        createdSlicer.Should().NotBeNull();
        createdSlicer!.Name.Should().Be(dto.Name);
        createdSlicer.SlicerType.Should().Be(dto.SlicerType);
        createdSlicer.Version.Should().Be(dto.Version);
        createdSlicer.Host.Should().Be(dto.Host);
        createdSlicer.Status.Should().Be("Online");
        createdSlicer.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RegisterAsync_WithNullName_UsesDefaultName()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = null!, // Explicitly null to trigger default
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        // Act
        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert
        SlicerService? createdSlicer = await context.Set<SlicerService>().FindAsync(id);
        createdSlicer.Should().NotBeNull();
        createdSlicer!.Name.Should().Be("orca-service");
    }

    [Fact]
    public async Task RegisterAsync_EnforcesMaxConcurrentJobsLimit()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-unlimited",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 1000 // Attempt to exceed limit
        };

        // Act
        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert - Should be capped by global settings
        SlicerService? createdSlicer = await context.Set<SlicerService>().FindAsync(id);
        createdSlicer.Should().NotBeNull();
        createdSlicer!.MaxConcurrentJobs.Should().BeLessThanOrEqualTo(100); // Typical global limit
    }

    [Fact]
    public async Task RegisterAsync_SynchronizesWorkerRecord()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-worker-sync",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 8
        };

        // Act
        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert - Check Worker record created
        Worker? worker = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        worker.Should().NotBeNull();
        worker!.Name.Should().Be(dto.Name);
        worker.EndpointUrl.Should().Be(dto.Host);
        worker.TotalSlots.Should().Be(1); // Limited by global settings
        worker.Status.Should().Be("Online");
    }

    #endregion

    #region ListAsync Tests

    [Fact]
    public async Task ListAsync_WithNoSlicers_ReturnsEmptyList()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        // Clean up any existing slicers
        context.Set<SlicerService>().RemoveRange(context.Set<SlicerService>());
        context.Set<Worker>().RemoveRange(context.Set<Worker>().Where(w => w.ServiceId != null));
        await context.SaveChangesAsync();

        // Act
        IReadOnlyList<SlicerService> slicers = await slicersService.ListAsync(CancellationToken.None);

        // Assert
        slicers.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_WithMultipleSlicers_ReturnsAllSlicers()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        // Clean up existing data
        context.Set<SlicerService>().RemoveRange(context.Set<SlicerService>());
        context.Set<Worker>().RemoveRange(context.Set<Worker>().Where(w => w.ServiceId != null));
        await context.SaveChangesAsync();

        var dto1 = new RegisterSlicerDto
        {
            Name = "orca-1",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var dto2 = new RegisterSlicerDto
        {
            Name = "prusa-1",
            SlicerType = 0,
            Version = "2.5.0",
            Host = "http://localhost:8081",
            MaxConcurrentJobs = 3
        };

        await slicersService.RegisterAsync(dto1, CancellationToken.None);
        await slicersService.RegisterAsync(dto2, CancellationToken.None);

        // Act
        IReadOnlyList<SlicerService> slicers = await slicersService.ListAsync(CancellationToken.None);

        // Assert
        slicers.Should().HaveCount(2);
        slicers.Select(s => s.Name).Should().Contain(new[] { "orca-1", "prusa-1" });
    }

    #endregion

    #region GetAsync Tests

    [Fact]
    public async Task GetAsync_WithValidId_ReturnsSlicer()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-get",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        SlicerService? retrieved = await slicersService.GetAsync(id, CancellationToken.None);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("orca-get");
        retrieved.Id.Should().Be(id);
    }

    [Fact]
    public async Task GetAsync_WithInvalidId_ReturnsNull()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        // Act
        SlicerService? result = await slicersService.GetAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region HeartbeatAsync Tests

    [Fact]
    public async Task HeartbeatAsync_WithValidId_UpdatesLastSeen()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-heartbeat",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);
        DateTime createdAt = (await context.Set<SlicerService>().FindAsync(id))!.LastSeen;

        // Wait a moment to ensure time difference
        await Task.Delay(100);

        // Act
        var heartbeatDto = new HeartbeatDto
        {
            Status = "Busy",
            FreeSlots = 2
        };
        bool result = await slicersService.HeartbeatAsync(id, heartbeatDto, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        SlicerService? updated = await context.Set<SlicerService>().FindAsync(id);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be("Busy");
        updated.LastSeen.Should().BeAfter(createdAt);
    }

    [Fact]
    public async Task HeartbeatAsync_WithInvalidId_ReturnsFalse()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        var heartbeatDto = new HeartbeatDto
        {
            Status = "Online",
            FreeSlots = 5
        };

        // Act
        bool result = await slicersService.HeartbeatAsync(Guid.NewGuid(), heartbeatDto, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HeartbeatAsync_SynchronizesWorkerStatus()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-worker-heartbeat",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 10
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act - Send heartbeat with status and free slots
        var heartbeatDto = new HeartbeatDto
        {
            Status = "Busy",
            FreeSlots = 3
        };
        await slicersService.HeartbeatAsync(id, heartbeatDto, CancellationToken.None);

        // Assert - Check Worker record updated with status
        Worker? worker = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        worker.Should().NotBeNull();
        worker!.Status.Should().Be("Busy");
        worker.LastHeartbeat.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region DeregisterAsync Tests

    [Fact]
    public async Task DeregisterAsync_WithValidId_RemovesSlicer()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-dereg",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        bool result = await slicersService.DeregisterAsync(id, retainForReregistration: false, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        SlicerService? deleted = await context.Set<SlicerService>().FindAsync(id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeregisterAsync_WithInvalidId_ReturnsFalse()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        // Act
        bool result = await slicersService.DeregisterAsync(Guid.NewGuid(), retainForReregistration: false, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeregisterAsync_RevokesWorkerCredentials()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-worker-dereg",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        await slicersService.DeregisterAsync(id, retainForReregistration: false, CancellationToken.None);

        // Assert - Check Worker record marked offline
        Worker? worker = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        worker.Should().NotBeNull();
        worker!.Status.Should().Be("Offline");
        worker.OfflineAt.Should().NotBeNull();
        worker.IsDisabled.Should().BeTrue();
        worker.ApiKey.Should().BeNull();
    }

    /// <summary>
    /// The redeploy regression: a worker with a stable InstanceId that deregisters on graceful
    /// shutdown and comes back must be re-identified and updated in place, never added as a new
    /// worker. Deleting the service row on deregistration destroyed the only anchor the
    /// InstanceId upsert can match on, so every redeploy created a fresh service Guid and — since
    /// Worker rows are keyed by that Guid — a fresh Worker row, orphaning the previous one as
    /// "Disabled: Slicer service deregistered" forever.
    /// </summary>
    [Fact]
    public async Task Redeploy_WithStableInstanceId_ReusesSameServiceAndWorkerRows()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        const string instanceId = "orcaslicer-worker-1";

        RegisterSlicerDto Dto() => new()
        {
            Name = "orca-redeploy",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = instanceId
        };

        (Guid firstId, string _) = await slicersService.RegisterAsync(Dto(), CancellationToken.None);
        Worker? firstWorker = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == firstId.ToString());
        firstWorker.Should().NotBeNull();
        Guid firstWorkerId = firstWorker!.Id;

        // Act - graceful shutdown, then the replacement container registers again
        bool deregistered = await slicersService.DeregisterAsync(firstId, retainForReregistration: true, CancellationToken.None);
        deregistered.Should().BeTrue();

        (Guid secondId, string _) = await slicersService.RegisterAsync(Dto(), CancellationToken.None);

        // Assert - same identity, and no duplicate rows accumulated
        secondId.Should().Be(firstId, "a stable InstanceId must be re-identified, not registered as a new worker");

        context.ChangeTracker.Clear();
        context.Set<SlicerService>().Count(s => s.InstanceId == instanceId).Should().Be(1);
        context.Set<Worker>().Count().Should().Be(1);

        Worker? secondWorker = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == secondId.ToString());
        secondWorker.Should().NotBeNull();
        secondWorker!.Id.Should().Be(firstWorkerId, "the existing Worker row must be reclaimed rather than replaced");
        secondWorker.Status.Should().Be("Online");
        secondWorker.IsDisabled.Should().BeFalse();
        secondWorker.DisabledReason.Should().BeNull("a reclaimed worker must not keep displaying stale disabled text");
        secondWorker.OfflineAt.Should().BeNull();
    }

    [Fact]
    public async Task DeregisterAsync_WithRetain_RetainsRowButRevokesCredentials()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-retain",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = "orcaslicer-worker-1"
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        await slicersService.DeregisterAsync(id, retainForReregistration: true, CancellationToken.None);

        // Assert - the row survives as the re-identification anchor, but cannot authenticate
        context.ChangeTracker.Clear();
        SlicerService? retained = await context.Set<SlicerService>().FindAsync(id);
        retained.Should().NotBeNull();
        retained!.Status.Should().Be("Offline");
        retained.ApiKey.Should().BeNull();

        Worker? worker = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        worker.Should().NotBeNull();
        worker!.IsDisabled.Should().BeTrue();
        worker.Status.Should().Be("Offline");
        worker.ApiKey.Should().BeNull();
    }

    /// <summary>
    /// An administrator's deliberate disable must survive a worker restart. Reclaiming a retained
    /// row lifts only the automatic disable that deregistration itself applied; if it lifted every
    /// disable, any banned worker could clear its own ban simply by re-registering under the same
    /// InstanceId, and the reason recording why it was banned would be erased with it.
    /// </summary>
    [Fact]
    public async Task Reregistration_PreservesAnAdministratorsDeliberateDisable()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        const string adminReason = "Banned by administrator: producing scrap";
        var dto = new RegisterSlicerDto
        {
            Name = "orca-banned",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = "orcaslicer-worker-banned"
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // An administrator bans the worker while it is running.
        Worker? banned = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        banned.Should().NotBeNull();
        banned!.IsDisabled = true;
        banned.DisabledReason = adminReason;
        banned.DisableSource = WorkerDisableSource.Administrator;

        // The worker then actually goes down. #1863 only lets a registration reclaim an
        // incumbent's identity once that incumbent is no longer live, so age the heartbeat past
        // the liveness window. Without this the re-registration below is indistinguishable from
        // a squatting attempt and is rejected before ban preservation is ever reached.
        banned.LastHeartbeat = DateTime.UtcNow.AddSeconds(-(WorkerStatus.LiveHeartbeatTimeoutSeconds + 30));
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Act - the worker restarts and re-registers under the same stable identity.
        (Guid secondId, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert - same row reclaimed, but the ban and its audit trail are intact.
        secondId.Should().Be(id);

        context.ChangeTracker.Clear();
        Worker? reclaimed = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        reclaimed.Should().NotBeNull();
        reclaimed!.IsDisabled.Should().BeTrue("an administrator's ban must survive a restart");
        reclaimed.DisabledReason.Should().Be(adminReason);
    }

    /// <summary>
    /// The redeploy path must not launder a ban. A graceful shutdown deregisters before the
    /// replacement registers, so if deregistration overwrote the administrator's reason with its
    /// own sentinel, the subsequent registration would read that sentinel, conclude the disable
    /// was its own automatic one, and lift the ban — letting a banned worker clear its ban with
    /// an ordinary redeploy. That is the most common path there is, so this is the case that
    /// matters most, not the crash path.
    /// </summary>
    [Fact]
    public async Task DeregisterThenReregister_PreservesAnAdministratorsDeliberateDisable()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        const string adminReason = "Banned by administrator: bad nozzle";
        var dto = new RegisterSlicerDto
        {
            Name = "orca-banned-redeploy",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = "orcaslicer-worker-banned-redeploy"
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        Worker? banned = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        banned.Should().NotBeNull();
        banned!.IsDisabled = true;
        banned.DisabledReason = adminReason;
        banned.DisableSource = WorkerDisableSource.Administrator;
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Act - a full redeploy: graceful shutdown deregisters, replacement re-registers.
        await slicersService.DeregisterAsync(id, retainForReregistration: true, CancellationToken.None);

        context.ChangeTracker.Clear();
        Worker? deregistered = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        deregistered.Should().NotBeNull();
        deregistered!.DisabledReason.Should().Be(
            adminReason,
            "deregistration must not overwrite an administrator's reason with its own sentinel");

        (Guid secondId, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert
        secondId.Should().Be(id);

        context.ChangeTracker.Clear();
        Worker? reclaimed = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        reclaimed.Should().NotBeNull();
        reclaimed!.IsDisabled.Should().BeTrue("a ban must survive a redeploy, not just a crash");
        reclaimed.DisabledReason.Should().Be(adminReason);
        reclaimed.DisableSource.Should().Be(WorkerDisableSource.Administrator);
    }

    /// <summary>
    /// An administrator's reason is unvalidated free text, so it can be made to look like any
    /// automatic disabler's. If classification read the reason text, an administrator who happened
    /// to type the deregistration literal would produce a ban the next registration silently
    /// lifted — the ban would be undone by the wording used to record it. Attribution therefore
    /// lives in a column the administrator does not control.
    /// </summary>
    [Fact]
    public async Task Reregistration_PreservesABanWhoseReasonImpersonatesAnAutomaticDisable()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-banned-impersonating",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = "orcaslicer-worker-banned-impersonating"
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // The administrator's reason is byte-for-byte the text deregistration writes.
        Worker? banned = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        banned.Should().NotBeNull();
        banned!.IsDisabled = true;
        banned.DisabledReason = WorkerDisableReasons.Deregistered;
        banned.DisableSource = WorkerDisableSource.Administrator;

        // As above: the worker has to be non-live before its identity can be reclaimed at all.
        banned.LastHeartbeat = DateTime.UtcNow.AddSeconds(-(WorkerStatus.LiveHeartbeatTimeoutSeconds + 30));
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Act
        (Guid secondId, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert
        secondId.Should().Be(id);

        context.ChangeTracker.Clear();
        Worker? reclaimed = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        reclaimed.Should().NotBeNull();
        reclaimed!.IsDisabled.Should().BeTrue(
            "the reason text an administrator happens to type must not decide whether their ban holds");
        reclaimed.DisableSource.Should().Be(WorkerDisableSource.Administrator);
    }

    /// <summary>
    /// A ban committed after a deregistration request has already materialised the worker must
    /// still hold. A read-modify-write would write back the pre-ban state it loaded, re-attributing
    /// the disable to deregistration, and the next registration would then lift it — so the ban
    /// would be destroyed by a request that never saw it. The attribution is written by a
    /// conditional UPDATE evaluated by the database, which cannot observe a stale value.
    /// </summary>
    [Fact]
    public async Task Deregistration_DoesNotClobberABanCommittedAfterItLoadedTheWorker()
    {
        // Arrange
        using AsyncServiceScope requestScope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = requestScope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext requestContext = requestScope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        const string adminReason = "Banned by administrator: racing the redeploy";
        var dto = new RegisterSlicerDto
        {
            Name = "orca-banned-race",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = "orcaslicer-worker-banned-race"
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);
        requestContext.ChangeTracker.Clear();

        // The API key filter materialises the worker into the deregistration request's scope
        // before the action body runs, so this request now holds a pre-ban view of the row.
        Worker? preBanView = requestContext.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        preBanView.Should().NotBeNull();
        preBanView!.IsDisabled.Should().BeFalse();

        // Meanwhile an administrator bans the worker from a different request, and commits.
        using (AsyncServiceScope adminScope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext adminContext = adminScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            Worker? banned = adminContext.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
            banned.Should().NotBeNull();
            banned!.IsDisabled = true;
            banned.DisabledReason = adminReason;
            banned.DisableSource = WorkerDisableSource.Administrator;
            _ = await adminContext.SaveChangesAsync();
        }

        // Act - the deregistration proceeds, still holding its stale view.
        await slicersService.DeregisterAsync(id, retainForReregistration: true, CancellationToken.None);

        // Assert
        using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        SlicerDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker? afterDeregister = verifyContext.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        afterDeregister.Should().NotBeNull();
        afterDeregister!.DisableSource.Should().Be(
            WorkerDisableSource.Administrator,
            "a deregistration that never observed the ban must not re-attribute it to itself");
        afterDeregister.DisabledReason.Should().Be(adminReason);

        // The offline and credential-revocation half of deregistration still applies.
        afterDeregister.IsDisabled.Should().BeTrue();
        afterDeregister.Status.Should().Be(WorkerStatus.Offline);
        afterDeregister.ApiKey.Should().BeNull();

        // And the ban still holds when the worker comes back.
        (Guid secondId, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);
        secondId.Should().Be(id);

        using AsyncServiceScope finalScope = _factory.Services.CreateAsyncScope();
        SlicerDbContext finalContext = finalScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker? reclaimed = finalContext.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        reclaimed.Should().NotBeNull();
        reclaimed!.IsDisabled.Should().BeTrue("the ban survived deregistration, so it must survive the return too");
        reclaimed.DisabledReason.Should().Be(adminReason);
    }

    /// <summary>
    /// A ban committed after a registration request has already materialised the worker must still
    /// hold. Deciding in memory whether the disable is automatic reads the snapshot taken when the
    /// row was loaded, and saving that instance would write IsDisabled = false straight over the
    /// ban — a worker would clear a sanction simply by registering with the right timing.
    /// Re-reading does not help: EF returns the same stale tracked instance. So the test and the
    /// write happen together in one conditional UPDATE the database evaluates.
    /// </summary>
    [Fact]
    public async Task Reregistration_DoesNotClobberABanCommittedAfterItLoadedTheWorker()
    {
        // Arrange
        using AsyncServiceScope setupScope = _factory.Services.CreateAsyncScope();
        ISlicersService setupService = setupScope.ServiceProvider.GetRequiredService<ISlicersService>();

        const string adminReason = "Banned by administrator: racing the re-registration";
        var dto = new RegisterSlicerDto
        {
            Name = "orca-banned-reregister-race",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = "orcaslicer-worker-banned-reregister-race"
        };

        (Guid id, string _) = await setupService.RegisterAsync(dto, CancellationToken.None);

        // A graceful redeploy leaves the worker disabled by deregistration — an automatic disable,
        // so the registration below is entitled to lift it.
        await setupService.DeregisterAsync(id, retainForReregistration: true, CancellationToken.None);

        // The replacement worker's registration request materialises the row before it decides
        // anything, so it now holds a view in which the disable is merely automatic.
        using AsyncServiceScope requestScope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = requestScope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext requestContext = requestScope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        Worker? preBanView = requestContext.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        preBanView.Should().NotBeNull();
        preBanView!.DisableSource.Should().Be(WorkerDisableSource.Deregistration);

        // Meanwhile an administrator bans the worker from a different request, and commits.
        using (AsyncServiceScope adminScope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext adminContext = adminScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            Worker? banned = adminContext.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
            banned.Should().NotBeNull();
            banned!.IsDisabled = true;
            banned.DisabledReason = adminReason;
            banned.DisableSource = WorkerDisableSource.Administrator;
            _ = await adminContext.SaveChangesAsync();
        }

        // Act - the registration proceeds, still holding its stale view.
        (Guid secondId, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert
        secondId.Should().Be(id);

        using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        SlicerDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker? reclaimed = verifyContext.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        reclaimed.Should().NotBeNull();
        reclaimed!.IsDisabled.Should().BeTrue(
            "a registration that never observed the ban must not lift it");
        reclaimed.DisabledReason.Should().Be(adminReason);
        reclaimed.DisableSource.Should().Be(WorkerDisableSource.Administrator);
    }

    /// <summary>
    /// The counterpart to <see cref="Reregistration_PreservesAnAdministratorsDeliberateDisable"/>:
    /// the automatic disable deregistration applies is lifted on reclaim, so a redeployed worker
    /// does not come back Online still reporting "Disabled: Slicer service deregistered" — the
    /// stale text operators saw after every redeploy.
    /// </summary>
    [Fact]
    public async Task Reregistration_ClearsTheDisableLeftByDeregistration()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-redeployed",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = "orcaslicer-worker-redeployed"
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);
        await slicersService.DeregisterAsync(id, retainForReregistration: true, CancellationToken.None);

        // Act
        (Guid secondId, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert
        secondId.Should().Be(id);

        context.ChangeTracker.Clear();
        Worker? reclaimed = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        reclaimed.Should().NotBeNull();
        reclaimed!.IsDisabled.Should().BeFalse();
        reclaimed.DisabledReason.Should().BeNull();
        reclaimed.OfflineAt.Should().BeNull();
        reclaimed.Status.Should().Be("Online");
    }

    /// <summary>
    /// A worker that did not ask for retention is deleted even when it sent an InstanceId. The
    /// worker always sends one — it falls back to a random per-process GUID when no stable ID is
    /// configured — so retention must be driven by the caller's declaration, not by the mere
    /// presence of an InstanceId. Retaining throwaway identities would strand one unreclaimable
    /// row per process start.
    /// </summary>
    [Fact]
    public async Task DeregisterAsync_WithoutRetain_DeletesRowEvenWithInstanceId()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-ephemeral",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = Guid.NewGuid().ToString("N")
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        await slicersService.DeregisterAsync(id, retainForReregistration: false, CancellationToken.None);

        // Assert
        context.ChangeTracker.Clear();
        SlicerService? deleted = await context.Set<SlicerService>().FindAsync(id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task PurgeAsync_RemovesServiceAndPairedWorkerRow()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-purge",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5,
            InstanceId = "orcaslicer-worker-1"
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act - the admin action means permanent removal even for a stable identity
        bool purged = await slicersService.PurgeAsync(id, CancellationToken.None);

        // Assert - no orphaned Worker row is left behind
        purged.Should().BeTrue();
        context.ChangeTracker.Clear();
        (await context.Set<SlicerService>().FindAsync(id)).Should().BeNull();
        context.Set<Worker>().Count(w => w.ServiceId == id.ToString()).Should().Be(0);
    }

    [Fact]
    public async Task PurgeAsync_WithInvalidId_ReturnsFalse()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        // Act
        bool result = await slicersService.PurgeAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region RotateApiKeyAsync Tests

    [Fact]
    public async Task RotateApiKeyAsync_WithValidId_GeneratesNewApiKey()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-rotate",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        (Guid id, string? originalKey) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        string? newKey = await slicersService.RotateApiKeyAsync(id, CancellationToken.None);

        // Assert
        newKey.Should().NotBeNull();
        newKey.Should().NotBe(originalKey);
        newKey.Should().NotContain("="); // Base64 padding removed

        SlicerService? updated = await context.Set<SlicerService>().FindAsync(id);
        updated!.ApiKey.Should().Be(newKey);
        updated.ApiKeyRotatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RotateApiKeyAsync_WithInvalidId_ReturnsNull()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        // Act
        string? result = await slicersService.RotateApiKeyAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RotateApiKeyAsync_SynchronizesWorkerApiKey()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-rotate-worker",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        (Guid id, string _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        string? newKey = await slicersService.RotateApiKeyAsync(id, CancellationToken.None);

        // Assert - Check Worker record updated
        Worker? worker = context.Set<Worker>().FirstOrDefault(w => w.ServiceId == id.ToString());
        worker.Should().NotBeNull();
        worker!.ApiKey.Should().Be(newKey);
    }

    [Fact]
    public async Task RotateApiKeyAsync_WithAdminForce_SuccessfullyRotates()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-admin-rotate",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        (Guid id, string? originalKey) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act - Rotate with admin force
        string? newKey = await slicersService.RotateApiKeyAsync(id, CancellationToken.None, isAdminForced: true);

        // Assert
        newKey.Should().NotBeNull();
        newKey.Should().NotBe(originalKey);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task FullLifecycle_RegisterHeartbeatDeregister()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        // Clean up any data from previous test
        context.Set<SlicerService>().RemoveRange(context.Set<SlicerService>());
        context.Set<Worker>().RemoveRange(context.Set<Worker>().Where(w => w.ServiceId != null));
        await context.SaveChangesAsync();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-lifecycle",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 8
        };

        // Act 1: Register
        (Guid id, string? _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert 1
        SlicerService? registered = await slicersService.GetAsync(id, CancellationToken.None);
        registered.Should().NotBeNull();
        registered!.Name.Should().Be("orca-lifecycle");

        // Act 2: Send heartbeat
        var heartbeatDto = new HeartbeatDto { Status = "Busy", FreeSlots = 4 };
        bool heartbeatResult = await slicersService.HeartbeatAsync(id, heartbeatDto, CancellationToken.None);

        // Assert 2
        heartbeatResult.Should().BeTrue();
        SlicerService? afterHeartbeat = await context.Set<SlicerService>().FindAsync(id);
        afterHeartbeat!.Status.Should().Be("Busy");

        // Act 3: Deregister
        bool deregisterResult = await slicersService.DeregisterAsync(id, retainForReregistration: false, CancellationToken.None);

        // Assert 3
        deregisterResult.Should().BeTrue();
        SlicerService? afterDeregister = await context.Set<SlicerService>().FindAsync(id);
        afterDeregister.Should().BeNull();
    }

    [Fact]
    public async Task MultipleSlicerTypes_RegisterAndList()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicersService slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        // Clean up existing data
        context.Set<SlicerService>().RemoveRange(context.Set<SlicerService>());
        context.Set<Worker>().RemoveRange(context.Set<Worker>().Where(w => w.ServiceId != null));
        await context.SaveChangesAsync();

        var orcaDto = new RegisterSlicerDto
        {
            Name = "orca-multi",
            SlicerType = 1, // OrcaSlicer
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var prusaDto = new RegisterSlicerDto
        {
            Name = "prusa-multi",
            SlicerType = 0, // PrusaSlicer
            Version = "2.5.0",
            Host = "http://localhost:8081",
            MaxConcurrentJobs = 3
        };

        var curaDto = new RegisterSlicerDto
        {
            Name = "cura-multi",
            SlicerType = 2, // Cura
            Version = "5.4.0",
            Host = "http://localhost:8082",
            MaxConcurrentJobs = 4
        };

        // Act
        await slicersService.RegisterAsync(orcaDto, CancellationToken.None);
        await slicersService.RegisterAsync(prusaDto, CancellationToken.None);
        await slicersService.RegisterAsync(curaDto, CancellationToken.None);

        IReadOnlyList<SlicerService> allSlicers = await slicersService.ListAsync(CancellationToken.None);

        // Assert
        allSlicers.Should().HaveCount(3);
        allSlicers.Select(s => s.SlicerType).Should().Contain(new[] { 0, 1, 2 });
        allSlicers.Select(s => s.Name).Should().Contain(new[] { "orca-multi", "prusa-multi", "cura-multi" });
    }

    #endregion
}
