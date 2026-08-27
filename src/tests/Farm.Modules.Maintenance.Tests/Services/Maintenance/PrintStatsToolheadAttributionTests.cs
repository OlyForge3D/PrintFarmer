using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Modules.Maintenance.Services.Maintenance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.Maintenance.Tests.Services.Maintenance;

public class PrintStatsToolheadAttributionTests
{
    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_ExternalSyncFailed_DoesNotIncrementToolheads()
    {
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            Guid.NewGuid(),
            statsExisted: true,
            externalSyncSuccess: false,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 12,
            repository.Object,
            CancellationToken.None);

        credited.Should().BeEmpty();
        repository.Verify(
            candidate => candidate.ApplyToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<ToolheadHourAttribution>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_UnchangedExternalHoursAcrossCycles_DoesNotIncrementToolheads()
    {
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);
        Guid printerId = Guid.NewGuid();

        for (int cycle = 0; cycle < 2; cycle++)
        {
            IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
                printerId,
                statsExisted: true,
                externalSyncSuccess: true,
                perToolMaintenanceEnabled: true,
                supportsPerToolAttribution: true,
                externalDelta: 0,
                repository.Object,
                CancellationToken.None);

            credited.Should().BeEmpty();
        }

        repository.Verify(
            candidate => candidate.ApplyToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<ToolheadHourAttribution>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_FeatureDisabled_DoesNotIncrementToolheads()
    {
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            Guid.NewGuid(),
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: false,
            supportsPerToolAttribution: true,
            externalDelta: 4,
            repository.Object,
            CancellationToken.None);

        credited.Should().BeEmpty();
        repository.Verify(
            candidate => candidate.ApplyToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<ToolheadHourAttribution>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyToolheadHoursAsync_ExclusiveActiveTool_CreditsOnlyThatToolhead()
    {
        // Issue #711 round-7 Finding 3: with T0 doing all the printing, only T0 should accrue wear;
        // T1 must NOT be advanced by an equal split.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 10);
        Toolhead t1 = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 4);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        var repository = new EfToolheadStatisticsRepository(db);

        ToolheadHourAttribution attribution = ToolheadHourAttribution.FromWeights(
            new Dictionary<Guid, double> { [t0.Id] = 6, [t1.Id] = 0 });
        attribution.IsApproximated.Should().BeFalse();

        IReadOnlyList<Guid> credited = await repository.ApplyToolheadHoursAsync(
            printerId,
            attribution,
            CancellationToken.None);
        await db.SaveChangesAsync();

        credited.Should().BeEquivalentTo([t0.Id]);
        t0.CumulativePrintHours.Should().Be(16);
        t1.CumulativePrintHours.Should().Be(4, "an idle toolhead must not accrue wear");
    }

    [Fact]
    public async Task ApplyToolheadHoursAsync_MixedActivity_SplitsProportionally()
    {
        // Issue #711 round-7 Finding 3: proportional per-tool weights credit each toolhead by its
        // actual share of the work.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 0);
        Toolhead t1 = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 0);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        var repository = new EfToolheadStatisticsRepository(db);

        ToolheadHourAttribution attribution = ToolheadHourAttribution.FromWeights(
            new Dictionary<Guid, double> { [t0.Id] = 7.0, [t1.Id] = 3.0 });

        IReadOnlyList<Guid> credited = await repository.ApplyToolheadHoursAsync(
            printerId,
            attribution,
            CancellationToken.None);
        await db.SaveChangesAsync();

        credited.Should().BeEquivalentTo([t0.Id, t1.Id]);
        t0.CumulativePrintHours.Should().BeApproximately(7.0, 0.0001);
        t1.CumulativePrintHours.Should().BeApproximately(3.0, 0.0001);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_CapabilityOff_NoTelemetry_LeavesUnattributedWithDiagnostic()
    {
        // Issue #711 round-10 Finding 1: a backend without per-tool attribution capability must NOT
        // fabricate per-toolhead wear by equal-splitting the external delta across idle heads. No
        // wear is credited and an operator-visible diagnostic is emitted.
        Mock<IToolheadStatisticsRepository> repository = new(MockBehavior.Strict);
        Mock<ILogger> logger = new();

        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            Guid.NewGuid(),
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: false,
            externalDelta: 8,
            repository.Object,
            CancellationToken.None,
            logger.Object);

        credited.Should().BeEmpty();

        // No repository access at all: the capability short-circuit must return before any query or
        // mutation (strict mock would throw on any unexpected call).
        repository.Verify(
            candidate => candidate.ApplyToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<ToolheadHourAttribution>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("unattributed")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task AttributeExternalToolheadHoursAsync_CapabilityOn_NoTelemetry_LeavesUnattributedWithDiagnostic()
    {
        // Issue #711 round-10 Finding 1: even a capable backend that produces no fresh active-tool
        // telemetry this cycle must leave the delta unattributed rather than equal-split it. Idle
        // heads keep their hours and a diagnostic is emitted.
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead t0 = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 5);
        Toolhead t1 = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 3);
        db.Toolheads.AddRange(t0, t1);
        await db.SaveChangesAsync();
        var repository = new EfToolheadStatisticsRepository(db);
        Mock<ILogger> logger = new();

        // No status cache supplied → no fresh active-tool telemetry available.
        IReadOnlyList<Guid> credited = await PrintStatsSyncHostedService.AttributeExternalToolheadHoursAsync(
            printerId,
            statsExisted: true,
            externalSyncSuccess: true,
            perToolMaintenanceEnabled: true,
            supportsPerToolAttribution: true,
            externalDelta: 8,
            repository,
            CancellationToken.None,
            logger.Object);
        await db.SaveChangesAsync();

        credited.Should().BeEmpty();
        t0.CumulativePrintHours.Should().Be(5, "no telemetry means no fabricated wear");
        t1.CumulativePrintHours.Should().Be(3, "no telemetry means no fabricated wear");

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("unattributed")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void FromWeights_PartialKnownActivity_LeavesUnknownResidualUncredited()
    {
        Guid knownToolhead = Guid.NewGuid();

        ToolheadHourAttribution attribution = ToolheadHourAttribution.FromWeights(
            new Dictionary<Guid, double> { [knownToolhead] = 0.4 },
            sourceHours: 10);

        attribution.Weights.Should().ContainSingle().Which.Value.Should().Be(0.4);
        attribution.Hours.Should().ContainSingle().Which.Value.Should().Be(4);
        attribution.TotalHours.Should().Be(4);
        attribution.SourceHours.Should().Be(10);
        attribution.IsApproximated.Should().BeFalse();
    }

    private static Toolhead CreateToolhead(
        Guid printerId,
        ToolheadType type,
        int index,
        double cumulativeHours)
    {
        return new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = $"Toolhead {index}",
            Index = index,
            ToolheadType = type,
            CumulativePrintHours = cumulativeHours
        };
    }
}
