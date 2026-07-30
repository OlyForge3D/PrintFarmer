using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Repositories.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Attention.Sources;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Attention;

public class HarvestAttentionSourceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public HarvestAttentionSourceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        _ = db.Database.EnsureCreated();
        var factory = new Mock<IDbContextFactory<AppDbContext>>();
        factory.Setup(value => value.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        _factory = factory.Object;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetItemsAsync_CompletedUnharvestedJob_ReturnsStableHarvestAction()
    {
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        await using (var db = new AppDbContext(_options))
        {
            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            _ = db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "Test Manufacturer" });
            _ = db.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                Name = "Test Model",
                ManufacturerId = manufacturerId,
            });
            _ = db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "Printer A",
                ServerUrl = "http://printer-a",
                BackendPort = 7125,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });
            _ = db.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                Name = "bracket.gcode",
                Status = PrintJobStatus.Completed,
                AssignedPrinterId = printerId,
                ActualEndTime = DateTime.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await db.SaveChangesAsync();
        }

        var gate = new Mock<IOperatorFeatureGate>();
        gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(true);
        gate.Setup(value => value.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var source = new HarvestAttentionSource(_factory, gate.Object);

        AttentionItemDto item = Assert.Single(await source.GetItemsAsync(CancellationToken.None));

        Assert.Equal(AttentionIdPrefixes.Build(AttentionIdPrefixes.Harvest, jobId), item.Id);
        Assert.Equal(AttentionKind.Harvest, item.Kind);
        Assert.Equal(jobId, item.JobId);
        Assert.Contains(item.Actions, action => action.Kind == AttentionActionKind.Harvest);
    }

    [Fact]
    public async Task GetItemsAsync_FeatureDisabled_DoesNotOpenDatabase()
    {
        var factory = new Mock<IDbContextFactory<AppDbContext>>(MockBehavior.Strict);
        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Strict);
        gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(false);
        gate.Setup(value => value.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var source = new HarvestAttentionSource(factory.Object, gate.Object);

        IReadOnlyList<AttentionItemDto> items = await source.GetItemsAsync(CancellationToken.None);

        Assert.Empty(items);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetItemsWithOriginAsync_ExtraRowSentinel_MarksCappedObservationIncomplete()
    {
        Guid printerId = Guid.NewGuid();
        await using (var db = new AppDbContext(_options))
        {
            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            _ = db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "Test Manufacturer" });
            _ = db.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                Name = "Test Model",
                ManufacturerId = manufacturerId,
            });
            _ = db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "Printer A",
                ServerUrl = "http://printer-a",
                BackendPort = 7125,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });
            for (int index = 0; index < 101; index++)
            {
                _ = db.PrintJobs.Add(new PrintJob
                {
                    Id = Guid.NewGuid(),
                    Name = $"part-{index}.gcode",
                    Status = PrintJobStatus.Completed,
                    AssignedPrinterId = printerId,
                    ActualEndTime = DateTime.UtcNow.AddMinutes(-index),
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            _ = await db.SaveChangesAsync();
        }

        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(value => value.IsEnabledStrictAsync(
                OperatorFeature.PrintedPartsInventory,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        HarvestAttentionSource source = new(
            _factory,
            gate.Object,
            new ConstantWatermarkReader(12));

        AttentionSourceResult result =
            await source.GetItemsWithOriginAsync(CancellationToken.None);

        Assert.Equal(100, result.Items.Count);
        Assert.False(result.IsAuthoritativeComplete);
        Assert.Contains("harvest-item-cap", result.IncompleteReasons);
    }

    [Fact]
    public async Task GetItemsWithOriginAsync_FeatureChangesDuringObservation_Throws()
    {
        Mock<IOperatorFeatureGate> gate = new();
        gate.SetupSequence(value => value.IsEnabledStrictAsync(
                OperatorFeature.PrintedPartsInventory,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        HarvestAttentionSource source = new(
            _factory,
            gate.Object,
            new ConstantWatermarkReader(12));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetItemsWithOriginAsync(CancellationToken.None));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteActionAsync_HarvestItem_DispatchesProductionHarvestService()
    {
        Guid jobId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        var source = new Mock<IAttentionSource>();
        source.SetupGet(value => value.SourceName).Returns("harvest");
        source.Setup(value => value.GetItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new AttentionItemDto(
                    AttentionIdPrefixes.Build(AttentionIdPrefixes.Harvest, jobId),
                    AttentionKind.Harvest,
                    AttentionSeverity.Info,
                    Guid.NewGuid(),
                    "Printer",
                    "Harvest",
                    "Harvest completed plate",
                    DateTime.UtcNow,
                    [new AttentionActionDto(AttentionActionKind.Harvest, "Harvest", true)],
                    JobId: jobId),
            ]);
        var harvest = new Mock<IPartHarvestService>(MockBehavior.Strict);
        harvest.Setup(value => value.HarvestJobAsync(
                jobId,
                It.IsAny<HarvestJobRequest>(),
                userId.ToString("D"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HarvestResult(
                PartInventoryOutcome.Ok,
                new HarvestJobResponse(jobId, DateTime.UtcNow, null, null, false, [], []),
                null));
        var gate = new Mock<IOperatorFeatureGate>();
        gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(true);
        gate.Setup(value => value.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new AttentionService(
            [source.Object],
            new Mock<IAttentionSnoozeRepository>().Object,
            new Mock<IPrintersService>().Object,
            new Mock<IMaintenanceAlertService>().Object,
            new Mock<IQueueDataService>().Object,
            NullLogger<AttentionService>.Instance,
            partHarvestService: harvest.Object,
            featureGate: gate.Object);

        AttentionActionResult result = await service.ExecuteActionAsync(
            userId,
            "operator",
            isFarmAdmin: false,
            AttentionIdPrefixes.Build(AttentionIdPrefixes.Harvest, jobId),
            AttentionActionKind.Harvest);

        Assert.Equal(AttentionActionOutcome.Ok, result.Outcome);
        harvest.VerifyAll();
    }

    private sealed class ConstantWatermarkReader(long value) : IMutationWatermarkReader
    {
        public Task<long> GetCurrentAsync(CancellationToken ct = default)
            => Task.FromResult(value);
    }
}
