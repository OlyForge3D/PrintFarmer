using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Modules.PrintQueue.Services.PrintQueue;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.PrintQueue.Tests.Services;

public class PrintJobManagementServiceQueuePlanningTests
{
    [Fact]
    public async Task GetModelStatsAsync_UsesInjectedTimeProviderForAnalyticsCutoff()
    {
        DateTime expectedNow = new(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        FakeTimeProvider clock = new(expectedNow);
        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetModelStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetCompletedJobsForAnalyticsAsync(
                null,
                expectedNow.AddDays(-30),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        PrintJobManagementService service = CreateService(repository, settingsService: null, clock);

        await service.GetModelStatsAsync();

        repository.Verify(r => r.GetCompletedJobsForAnalyticsAsync(
            null,
            expectedNow.AddDays(-30),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetQueueStatsAsync_WithWorkingHoursSettings_ReturnsNaiveAndStaffedCompletion()
    {
        DateTime now = new(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        Guid printerId = Guid.NewGuid();

        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetQueueStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((queued: 1, printing: 1, paused: 0, completed: 0, failed: 0));
        repository.Setup(r => r.GetAverageWaitTimeMinutesAsync(null, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(12);
        repository.Setup(r => r.GetFilteredJobsAsync(
                null,
                null,
                null,
                null,
                null,
                "priority",
                5000,
                0,
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PrintJob
                {
                    Id = Guid.NewGuid(),
                    Status = PrintJobStatus.Printing,
                    AssignedPrinterId = printerId,
                    Priority = 10,
                    QueuePosition = 1,
                    ActualStartTime = now.AddMinutes(-30),
                    EstimatedPrintTime = TimeSpan.FromHours(2),
                    QueuedAt = now.AddHours(-1),
                    CreatedAt = now.AddHours(-1),
                    UpdatedAt = now.AddMinutes(-5)
                },
                new PrintJob
                {
                    Id = Guid.NewGuid(),
                    Status = PrintJobStatus.Queued,
                    AssignedPrinterId = printerId,
                    Priority = 5,
                    QueuePosition = 2,
                    EstimatedPrintTime = TimeSpan.FromHours(1),
                    QueuedAt = now.AddMinutes(-10),
                    CreatedAt = now.AddMinutes(-10),
                    UpdatedAt = now.AddMinutes(-1)
                }
            ]);

        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<QueuePlanningSettings>())
            .Returns(new QueuePlanningSettings
            {
                WorkdayStartHourUtc = now.Hour,
                WorkdayEndHourUtc = (now.Hour + 1) % 24,
                BedClearMinutes = 10,
                DefaultDeadlineHours = 24,
                RequireDeadline = true,
                MinimumLeadHours = 4
            });

        PrintJobManagementService service = CreateService(repository, settingsService);

        var result = await service.GetQueueStatsAsync();

        Assert.NotNull(result.EstimatedQueueCompletionUtc);
        Assert.NotNull(result.StaffedCompletionUtc);
        Assert.True(result.StaffedCompletionUtc > result.EstimatedQueueCompletionUtc);
        Assert.Equal(10, result.Assumptions.BedClearMinutes);
        Assert.Equal(now.Hour, result.Assumptions.WorkdayStartHourUtc);
        Assert.Equal((now.Hour + 1) % 24, result.Assumptions.WorkdayEndHourUtc);
        Assert.Equal(24, result.Assumptions.DefaultDeadlineHours);
        Assert.True(result.Assumptions.RequireDeadline);
        Assert.Equal(4, result.Assumptions.MinimumLeadHours);
    }

    [Fact]
    public async Task GetQueueStatsAsync_WhenNoActiveJobs_ReturnsNullCompletionsWithDefaultAssumptions()
    {
        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetQueueStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((queued: 0, printing: 0, paused: 0, completed: 0, failed: 0));
        repository.Setup(r => r.GetAverageWaitTimeMinutesAsync(null, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        repository.Setup(r => r.GetFilteredJobsAsync(
                null,
                null,
                null,
                null,
                null,
                "priority",
                5000,
                0,
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        PrintJobManagementService service = CreateService(repository, settingsService: null);

        var result = await service.GetQueueStatsAsync();

        Assert.Null(result.EstimatedQueueCompletionUtc);
        Assert.Null(result.StaffedCompletionUtc);
        Assert.Equal(8, result.Assumptions.WorkdayStartHourUtc);
        Assert.Equal(17, result.Assumptions.WorkdayEndHourUtc);
        Assert.Equal(10, result.Assumptions.BedClearMinutes);
        Assert.Null(result.Assumptions.DefaultDeadlineHours);
        Assert.False(result.Assumptions.RequireDeadline);
        Assert.Equal(0, result.Assumptions.MinimumLeadHours);
    }

    [Fact]
    public async Task GetQueueStatsAsync_WhenQueuePlanningSettingsMissing_UsesStrictDeadlineFallback()
    {
        Mock<IPrintJobManagementRepository> repository = new();
        repository.Setup(r => r.GetQueueStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((queued: 0, printing: 0, paused: 0, completed: 0, failed: 0));
        repository.Setup(r => r.GetAverageWaitTimeMinutesAsync(null, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        repository.Setup(r => r.GetFilteredJobsAsync(
                null,
                null,
                null,
                null,
                null,
                "priority",
                5000,
                0,
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<QueuePlanningSettings>())
            .Returns((QueuePlanningSettings)null!);

        PrintJobManagementService service = CreateService(repository, settingsService);

        var result = await service.GetQueueStatsAsync();

        Assert.Null(result.EstimatedQueueCompletionUtc);
        Assert.Null(result.StaffedCompletionUtc);
        Assert.True(result.Assumptions.RequireDeadline);
        Assert.Null(result.Assumptions.DefaultDeadlineHours);
        Assert.Equal(0, result.Assumptions.MinimumLeadHours);
    }

    private static PrintJobManagementService CreateService(
        Mock<IPrintJobManagementRepository> repository,
        Mock<ISettingsService>? settingsService,
        TimeProvider? timeProvider = null)
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
            settingsService: settingsService?.Object,
            timeProvider: timeProvider);
    }

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = new(utcNow, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
