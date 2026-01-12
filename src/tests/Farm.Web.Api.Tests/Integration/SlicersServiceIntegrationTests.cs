using System;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Slicing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for SlicersService
/// Tests slicer registration, deregistration, heartbeat, API key rotation, and Worker synchronization
/// Fast executing (~2 seconds for 20 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
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
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
        var (id, apiKey) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert
        id.Should().NotBeEmpty();
        apiKey.Should().NotBeNullOrEmpty();
        apiKey.Should().NotContain("="); // Base64 padding removed

        var createdSlicer = await context.SlicerServices.FindAsync(id);
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
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = null!, // Explicitly null to trigger default
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        // Act
        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert
        var createdSlicer = await context.SlicerServices.FindAsync(id);
        createdSlicer.Should().NotBeNull();
        createdSlicer!.Name.Should().Be("orca-service");
    }

    [Fact]
    public async Task RegisterAsync_EnforcesMaxConcurrentJobsLimit()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-unlimited",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 1000 // Attempt to exceed limit
        };

        // Act
        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert - Should be capped by global settings
        var createdSlicer = await context.SlicerServices.FindAsync(id);
        createdSlicer.Should().NotBeNull();
        createdSlicer!.MaxConcurrentJobs.Should().BeLessThanOrEqualTo(100); // Typical global limit
    }

    [Fact]
    public async Task RegisterAsync_SynchronizesWorkerRecord()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-worker-sync",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 8
        };

        // Act
        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert - Check Worker record created
        var worker = context.Workers.FirstOrDefault(w => w.ServiceId == id.ToString());
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
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Clean up any existing slicers
        context.SlicerServices.RemoveRange(context.SlicerServices);
        context.Workers.RemoveRange(context.Workers.Where(w => w.ServiceId != null));
        await context.SaveChangesAsync();

        // Act
        var slicers = await slicersService.ListAsync(CancellationToken.None);

        // Assert
        slicers.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_WithMultipleSlicers_ReturnsAllSlicers()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Clean up existing data
        context.SlicerServices.RemoveRange(context.SlicerServices);
        context.Workers.RemoveRange(context.Workers.Where(w => w.ServiceId != null));
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
        var slicers = await slicersService.ListAsync(CancellationToken.None);

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
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-get",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        var retrieved = await slicersService.GetAsync(id, CancellationToken.None);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("orca-get");
        retrieved.Id.Should().Be(id);
    }

    [Fact]
    public async Task GetAsync_WithInvalidId_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        // Act
        var result = await slicersService.GetAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region HeartbeatAsync Tests

    [Fact]
    public async Task HeartbeatAsync_WithValidId_UpdatesLastSeen()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-heartbeat",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);
        var createdAt = (await context.SlicerServices.FindAsync(id))!.LastSeen;

        // Wait a moment to ensure time difference
        await Task.Delay(100);

        // Act
        var heartbeatDto = new HeartbeatDto
        {
            Status = "Busy",
            FreeSlots = 2
        };
        var result = await slicersService.HeartbeatAsync(id, heartbeatDto, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updated = await context.SlicerServices.FindAsync(id);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be("Busy");
        updated.LastSeen.Should().BeAfter(createdAt);
    }

    [Fact]
    public async Task HeartbeatAsync_WithInvalidId_ReturnsFalse()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        var heartbeatDto = new HeartbeatDto
        {
            Status = "Online",
            FreeSlots = 5
        };

        // Act
        var result = await slicersService.HeartbeatAsync(Guid.NewGuid(), heartbeatDto, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HeartbeatAsync_SynchronizesWorkerStatus()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-worker-heartbeat",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 10
        };

        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act - Send heartbeat with status and free slots
        var heartbeatDto = new HeartbeatDto
        {
            Status = "Busy",
            FreeSlots = 3
        };
        await slicersService.HeartbeatAsync(id, heartbeatDto, CancellationToken.None);

        // Assert - Check Worker record updated with status
        var worker = context.Workers.FirstOrDefault(w => w.ServiceId == id.ToString());
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
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-dereg",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        var result = await slicersService.DeregisterAsync(id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var deleted = await context.SlicerServices.FindAsync(id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeregisterAsync_WithInvalidId_ReturnsFalse()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        // Act
        var result = await slicersService.DeregisterAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeregisterAsync_MarksWorkerAsOffline()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-worker-dereg",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        await slicersService.DeregisterAsync(id, CancellationToken.None);

        // Assert - Check Worker record marked offline
        var worker = context.Workers.FirstOrDefault(w => w.ServiceId == id.ToString());
        worker.Should().NotBeNull();
        worker!.Status.Should().Be("Offline");
        worker.OfflineAt.Should().NotBeNull();
    }

    #endregion

    #region RotateApiKeyAsync Tests

    [Fact]
    public async Task RotateApiKeyAsync_WithValidId_GeneratesNewApiKey()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-rotate",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var (id, originalKey) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        var newKey = await slicersService.RotateApiKeyAsync(id, CancellationToken.None);

        // Assert
        newKey.Should().NotBeNull();
        newKey.Should().NotBe(originalKey);
        newKey.Should().NotContain("="); // Base64 padding removed

        var updated = await context.SlicerServices.FindAsync(id);
        updated!.ApiKey.Should().Be(newKey);
        updated.ApiKeyRotatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RotateApiKeyAsync_WithInvalidId_ReturnsNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        // Act
        var result = await slicersService.RotateApiKeyAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RotateApiKeyAsync_SynchronizesWorkerApiKey()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-rotate-worker",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var (id, _) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act
        var newKey = await slicersService.RotateApiKeyAsync(id, CancellationToken.None);

        // Assert - Check Worker record updated
        var worker = context.Workers.FirstOrDefault(w => w.ServiceId == id.ToString());
        worker.Should().NotBeNull();
        worker!.ApiKey.Should().Be(newKey);
    }

    [Fact]
    public async Task RotateApiKeyAsync_WithAdminForce_SuccessfullyRotates()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();

        var dto = new RegisterSlicerDto
        {
            Name = "orca-admin-rotate",
            SlicerType = 1,
            Version = "2.3.1",
            Host = "http://localhost:8080",
            MaxConcurrentJobs = 5
        };

        var (id, originalKey) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Act - Rotate with admin force
        var newKey = await slicersService.RotateApiKeyAsync(id, CancellationToken.None, isAdminForced: true);

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
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Clean up any data from previous test
        context.SlicerServices.RemoveRange(context.SlicerServices);
        context.Workers.RemoveRange(context.Workers.Where(w => w.ServiceId != null));
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
        var (id, apiKey) = await slicersService.RegisterAsync(dto, CancellationToken.None);

        // Assert 1
        var registered = await slicersService.GetAsync(id, CancellationToken.None);
        registered.Should().NotBeNull();
        registered!.Name.Should().Be("orca-lifecycle");

        // Act 2: Send heartbeat
        var heartbeatDto = new HeartbeatDto { Status = "Busy", FreeSlots = 4 };
        var heartbeatResult = await slicersService.HeartbeatAsync(id, heartbeatDto, CancellationToken.None);

        // Assert 2
        heartbeatResult.Should().BeTrue();
        var afterHeartbeat = await context.SlicerServices.FindAsync(id);
        afterHeartbeat!.Status.Should().Be("Busy");

        // Act 3: Deregister
        var deregisterResult = await slicersService.DeregisterAsync(id, CancellationToken.None);

        // Assert 3
        deregisterResult.Should().BeTrue();
        var afterDeregister = await context.SlicerServices.FindAsync(id);
        afterDeregister.Should().BeNull();
    }

    [Fact]
    public async Task MultipleSlicerTypes_RegisterAndList()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var slicersService = scope.ServiceProvider.GetRequiredService<ISlicersService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Clean up existing data
        context.SlicerServices.RemoveRange(context.SlicerServices);
        context.Workers.RemoveRange(context.Workers.Where(w => w.ServiceId != null));
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

        var allSlicers = await slicersService.ListAsync(CancellationToken.None);

        // Assert
        allSlicers.Should().HaveCount(3);
        allSlicers.Select(s => s.SlicerType).Should().Contain(new[] { 0, 1, 2 });
        allSlicers.Select(s => s.Name).Should().Contain(new[] { "orca-multi", "prusa-multi", "cura-multi" });
    }

    #endregion
}
