using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Slicer.Module.Tests;

public sealed class SlicerHostWorkerCompatibilityServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<SlicerDbContext> _options;

    public SlicerHostWorkerCompatibilityServiceTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(_connection)
            .Options;
        using SlicerDbContext setup = new(_options);
        _ = setup.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_NoOnlineServices_ReturnsEmpty()
    {
        SlicerHostWorkerCompatibilityService service = CreateService();

        WorkerCompatibilitySnapshotDto snapshot =
            await service.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.PinnedIdentity.Should().BeNull();
        _ = snapshot.ObservedVersions.Should().BeEmpty();
        _ = snapshot.HasSupportedVersion.Should().BeFalse();
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_HealthyAttestedWorker_ReturnsPinnedIdentity()
    {
        Guid serviceId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        string capabilities = AttestedCapabilitiesJson("digest-container", "digest-binary");
        await SeedAsync(serviceId, workerId, CalibrationContractConstants.SlicerVersion, capabilities);

        SlicerHostWorkerCompatibilityService service = CreateService();

        WorkerCompatibilitySnapshotDto snapshot =
            await service.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.HasSupportedVersion.Should().BeTrue();
        _ = snapshot.ObservedVersions.Should().Contain(CalibrationContractConstants.SlicerVersion);
        WorkerCompatibilityPinnedIdentityDto pinned = snapshot.PinnedIdentity.Should().NotBeNull().And.Subject
            as WorkerCompatibilityPinnedIdentityDto;
        _ = pinned!.WorkerId.Should().Be(workerId);
        _ = pinned.Version.Should().Be(CalibrationContractConstants.SlicerVersion);
        _ = pinned.Distribution.Should().Be(CalibrationContractConstants.SlicerDistribution);
        _ = pinned.ContainerDigest.Should().Be("digest-container");
        _ = pinned.BinarySha256.Should().Be("digest-binary");
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_VersionMismatch_ReturnsNoPinnedWorker()
    {
        Guid serviceId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        string capabilities = AttestedCapabilitiesJson("digest-container", "digest-binary");
        await SeedAsync(serviceId, workerId, CalibrationContractConstants.SlicerVersion, capabilities);

        SlicerHostWorkerCompatibilityService service = CreateService();

        WorkerCompatibilitySnapshotDto snapshot =
            await service.GetWorkerCompatibilityAsync("9.9.9", CancellationToken.None);

        _ = snapshot.PinnedIdentity.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_MissingDigests_ExcludesWorker()
    {
        Guid serviceId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        string capabilities = """{"capabilities":["orcaslicer-upstream"]}""";
        await SeedAsync(serviceId, workerId, CalibrationContractConstants.SlicerVersion, capabilities);

        SlicerHostWorkerCompatibilityService service = CreateService();

        WorkerCompatibilitySnapshotDto snapshot =
            await service.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.PinnedIdentity.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_DisabledWorker_ExcludesWorker()
    {
        Guid serviceId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        string capabilities = AttestedCapabilitiesJson("digest-container", "digest-binary");
        await SeedAsync(
            serviceId,
            workerId,
            CalibrationContractConstants.SlicerVersion,
            capabilities,
            isDisabled: true);

        SlicerHostWorkerCompatibilityService service = CreateService();

        WorkerCompatibilitySnapshotDto snapshot =
            await service.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.PinnedIdentity.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_StaleHeartbeat_ExcludesWorker()
    {
        Guid serviceId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        string capabilities = AttestedCapabilitiesJson("digest-container", "digest-binary");
        await SeedAsync(
            serviceId,
            workerId,
            CalibrationContractConstants.SlicerVersion,
            capabilities,
            heartbeatAge: TimeSpan.FromMinutes(10));

        SlicerHostWorkerCompatibilityService service = CreateService();

        WorkerCompatibilitySnapshotDto snapshot =
            await service.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.PinnedIdentity.Should().BeNull();
    }

    private static string AttestedCapabilitiesJson(string containerDigest, string binarySha256) =>
        $$"""
        {
          "capabilities": ["orcaslicer-upstream"],
          "slicerContainerDigest": "{{containerDigest}}",
          "slicerBinarySha256": "{{binarySha256}}"
        }
        """;

    private async Task SeedAsync(
        Guid serviceId,
        Guid workerId,
        string version,
        string capabilitiesJson,
        bool isDisabled = false,
        TimeSpan? heartbeatAge = null)
    {
        await using SlicerDbContext db = new(_options);
        DateTime now = DateTime.UtcNow;
        _ = db.SlicerServices.Add(new SlicerService
        {
            Id = serviceId,
            Name = "orca-1",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = version,
            Status = WorkerStatus.Online,
            LastSeen = now,
            CapabilitiesJson = capabilitiesJson,
        });
        _ = db.Workers.Add(new Worker
        {
            Id = workerId,
            ServiceId = serviceId.ToString(),
            Name = "orca-1-worker",
            EndpointUrl = "https://orca-1.internal",
            Status = WorkerStatus.Online,
            Version = version,
            CapabilitiesJson = capabilitiesJson,
            ApiKey = "test-key",
            IsDisabled = isDisabled,
            LastHeartbeat = now - (heartbeatAge ?? TimeSpan.Zero),
            RegisteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ = await db.SaveChangesAsync();
    }

    private SlicerHostWorkerCompatibilityService CreateService() =>
        new(
            new SharedConnectionSlicerDbContextFactory(_options),
            NullLogger<SlicerHostWorkerCompatibilityService>.Instance);

    private sealed class SharedConnectionSlicerDbContextFactory(DbContextOptions<SlicerDbContext> options)
        : IDbContextFactory<SlicerDbContext>
    {
        public SlicerDbContext CreateDbContext() => new(options);
    }
}
