using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class PrintJobManagementServiceQueueRecommendationsTests
{
    [Fact]
    public async Task GetQueueRecommendationsAsync_WithMixedConstraints_ReturnsPrioritizedRecommendations()
    {
        Mock<IPrintJobManagementRepository> repository = new();
        Mock<IDispatchScorer> scorer = new();
        PrintJobManagementService service = CreateService(repository, scorer);

        PrintJob materialJobA = CreateQueuedJob();
        PrintJob materialJobB = CreateQueuedJob();
        PrintJob nozzleJob = CreateQueuedJob();
        PrintJob bedClearJob = CreateQueuedJob();
        PrintJob idleOpportunityJob = CreateQueuedJob();

        repository
            .Setup(r => r.GetFilteredJobsAsync(
                null,
                null,
                null,
                null,
                null,
                "priority",
                500,
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([materialJobA, materialJobB, nozzleJob, bedClearJob, idleOpportunityJob]);

        scorer.Setup(s => s.ScorePrintersForJobAsync(materialJobA.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateEliminatedScore("Printer does not support material 'ASA'")]);
        scorer.Setup(s => s.ScorePrintersForJobAsync(materialJobB.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateEliminatedScore("Material 'ASA' requires an enclosure but printer has none")]);
        scorer.Setup(s => s.ScorePrintersForJobAsync(nozzleJob.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateEliminatedScore("No toolhead has nozzle diameter 0.60mm (±0.01mm)")]);
        scorer.Setup(s => s.ScorePrintersForJobAsync(bedClearJob.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateEliminatedScore("Printer is waiting for bed clear confirmation")]);
        scorer.Setup(s => s.ScorePrintersForJobAsync(idleOpportunityJob.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateIdleOpportunityScore()]);

        List<QueueRecommendationDto> recommendations = await service.GetQueueRecommendationsAsync();

        Assert.NotEmpty(recommendations);
        Assert.Equal("material-mismatch", recommendations[0].Category);
        Assert.Equal(2, recommendations[0].EstimatedUnlockedJobCount);

        QueueRecommendationDto? nozzle = recommendations.FirstOrDefault(r => r.Category == "nozzle-mismatch");
        QueueRecommendationDto? bedClear = recommendations.FirstOrDefault(r => r.Category == "bed-clear-blocking");
        QueueRecommendationDto? idle = recommendations.FirstOrDefault(r => r.Category == "idle-printer-opportunity");

        Assert.NotNull(nozzle);
        Assert.NotNull(bedClear);
        Assert.NotNull(idle);
        Assert.Equal(1, nozzle!.EstimatedUnlockedJobCount);
        Assert.Equal(1, bedClear!.EstimatedUnlockedJobCount);
        Assert.Equal(1, idle!.EstimatedUnlockedJobCount);
    }

    [Fact]
    public async Task GetQueueRecommendationsAsync_WhenDispatchScorerMissing_ReturnsEmptyList()
    {
        Mock<IPrintJobManagementRepository> repository = new();
        PrintJobManagementService service = CreateService(repository, scorer: null);

        List<QueueRecommendationDto> recommendations = await service.GetQueueRecommendationsAsync();

        Assert.Empty(recommendations);
        repository.Verify(
            r => r.GetFilteredJobsAsync(
                It.IsAny<PrintJobStatus?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static PrintJobManagementService CreateService(
        Mock<IPrintJobManagementRepository> repository,
        Mock<IDispatchScorer>? scorer)
    {
        return new PrintJobManagementService(
            repository.Object,
            NullLogger<PrintJobManagementService>.Instance,
            Mock.Of<IPrintersService>(),
            Mock.Of<IStoragePathService>(),
            Mock.Of<IHubContext<PrinterHub>>(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            notificationService: Mock.Of<INotificationService>(),
            retryService: Mock.Of<IRetryService>(),
            printerStatusRefreshService: Mock.Of<IPrinterStatusRefreshService>(),
            jobCostCalculationService: Mock.Of<IJobCostCalculationService>(),
            cameraSnapshotService: Mock.Of<ICameraSnapshotService>(),
            serviceScopeFactory: Mock.Of<IServiceScopeFactory>(),
            dispatchScorer: scorer?.Object);
    }

    private static PrintJob CreateQueuedJob()
    {
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "test-job",
            Status = PrintJobStatus.Queued,
            Priority = 0,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow
        };
    }

    private static DispatchScore CreateEliminatedScore(string reason)
    {
        return new DispatchScore(
            Guid.NewGuid(),
            "printer-a",
            0,
            [],
            true,
            [reason]);
    }

    private static DispatchScore CreateIdleOpportunityScore()
    {
        Dictionary<string, FactorScore> breakdown = new()
        {
            ["Availability"] = new FactorScore("Availability", 100, 0, 0, true),
            ["QueueDepth"] = new FactorScore("QueueDepth", 100, 30, 3000, false)
        };

        return new DispatchScore(
            Guid.NewGuid(),
            "printer-idle",
            92,
            breakdown,
            false,
            []);
    }
}
