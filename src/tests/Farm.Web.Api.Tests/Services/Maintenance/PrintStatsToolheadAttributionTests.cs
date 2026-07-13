using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Web.Api.Services.Maintenance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

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
            externalDelta: 12,
            repository.Object,
            CancellationToken.None);

        credited.Should().BeEmpty();
        repository.Verify(
            candidate => candidate.IncrementActiveToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<double>(),
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
                externalDelta: 0,
                repository.Object,
                CancellationToken.None);

            credited.Should().BeEmpty();
        }

        repository.Verify(
            candidate => candidate.IncrementActiveToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<double>(),
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
            externalDelta: 4,
            repository.Object,
            CancellationToken.None);

        credited.Should().BeEmpty();
        repository.Verify(
            candidate => candidate.IncrementActiveToolheadHoursAsync(
                It.IsAny<Guid>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IncrementActiveToolheadHoursAsync_MultiplePhysicalToolheads_SplitsDeltaEqually()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        Guid printerId = Guid.NewGuid();
        Toolhead primary = CreateToolhead(printerId, ToolheadType.Physical, index: 0, cumulativeHours: 10);
        Toolhead secondary = CreateToolhead(printerId, ToolheadType.Physical, index: 1, cumulativeHours: 4);
        Toolhead mmuGate = CreateToolhead(printerId, ToolheadType.MmuGate, index: 2, cumulativeHours: 7);
        db.Toolheads.AddRange(primary, secondary, mmuGate);
        await db.SaveChangesAsync();
        var repository = new EfToolheadStatisticsRepository(db);

        IReadOnlyList<Guid> credited = await repository.IncrementActiveToolheadHoursAsync(
            printerId,
            deltaHours: 6,
            CancellationToken.None);
        await db.SaveChangesAsync();

        credited.Should().BeEquivalentTo([primary.Id, secondary.Id]);
        primary.CumulativePrintHours.Should().Be(13);
        secondary.CumulativePrintHours.Should().Be(7);
        mmuGate.CumulativePrintHours.Should().Be(7);
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
