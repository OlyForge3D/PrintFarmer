using System.Reflection;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Infrastructure.Tests.Services.FailureDetection;

public sealed class PrintFailureMonitorPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public PrintFailureMonitorPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext dbContext = CreateDbContext();
        _ = dbContext.Database.EnsureCreated();
    }

    [Theory]
    [InlineData(false, false, "none", null, "none", null)]
    [InlineData(true, false, "global", "http://obico.local", "global", "http://obico.local")]
    [InlineData(false, true, "global", "http://obico.local", "printguard", "PrintGuard")]
    [InlineData(true, true, "global", "http://obico.local", "multiple", "http://obico.local + PrintGuard")]
    public void ResolveProviderTargets_ReportsOnlySelectedProviderTargets(
        bool obicoSelected,
        bool printGuardSelected,
        string obicoDetectionSource,
        string? obicoDetectionTarget,
        string expectedDetectionSource,
        string? expectedDetectionTarget)
    {
        MethodInfo method = typeof(PrintFailureMonitorService).GetMethod(
            "ResolveProviderTargets",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected ResolveProviderTargets private method.");

        object resultValue = method.Invoke(
            null,
            [obicoSelected, printGuardSelected, obicoDetectionSource, obicoDetectionTarget])
            ?? throw new InvalidOperationException("Expected ResolveProviderTargets to return a value.");
        (string DetectionSource, string? DetectionTarget) result =
            ((string DetectionSource, string? DetectionTarget))resultValue;

        result.DetectionSource.Should().Be(expectedDetectionSource);
        result.DetectionTarget.Should().Be(expectedDetectionTarget);
    }

    [Fact]
    public async Task HandleFailureDetectedAsync_PersistsIncidentAndBroadcastsEnrichedEvent()
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer printer = await SeedPrinterAsync(dbContext, "Monitor History Printer");
        IFailureDetectionIncidentHistoryService historyService = new FailureDetectionIncidentHistoryService(dbContext);

        var cachedStatus = new PrinterStatusDto(
            printer.Id,
            true,
            "Printing",
            JobName: "jobs/monitoring-job.gcode",
            FileName: "monitoring-job.gcode");

        Mock<IPrinterStatusCacheReader> statusCache = new();
        statusCache.Setup(cache => cache.GetStatus(printer.Id)).Returns(cachedStatus);

        Mock<IClientProxy> clientProxy = new();
        clientProxy
            .Setup(proxy => proxy.SendCoreAsync(
                "FailureDetected",
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IHubClients> hubClients = new();
        hubClients.Setup(clients => clients.Group(It.IsAny<string>())).Returns(clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.SetupGet(context => context.Clients).Returns(hubClients.Object);

        Mock<ISettingsService> settingsService = new();
        settingsService
            .Setup(service => service.Get<ObicoSettings>())
            .Returns(new ObicoSettings
            {
                Enabled = true,
                ScanIntervalSeconds = 30,
                ConfidenceThreshold = 0.8m,
            });

        Mock<IOptionsMonitor<PrintGuardShadowSettings>> optionsMonitor = new();
        optionsMonitor.SetupGet(options => options.CurrentValue).Returns(new PrintGuardShadowSettings());
        Mock<IServiceProvider> serviceProvider = new();
        serviceProvider.Setup(provider => provider.GetService(typeof(ISettingsService))).Returns(settingsService.Object);
        serviceProvider.Setup(provider => provider.GetService(typeof(IOptionsMonitor<PrintGuardShadowSettings>))).Returns(optionsMonitor.Object);

        Mock<IServiceScope> scope = new();
        scope.SetupGet(createdScope => createdScope.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(factory => factory.CreateScope()).Returns(scope.Object);

        FailureDetectionMonitorStatusStore monitorStatus = new();
        ILogger<PrintFailureMonitorService> logger = Mock.Of<ILogger<PrintFailureMonitorService>>();
        FailureDetectionMetrics metrics = new();

        PrintFailureMonitorService service = new(
            scopeFactory.Object,
            monitorStatus,
            statusCache.Object,
            hub.Object,
            metrics,
            logger);

        FailureDetectionResult result = FailureDetectionResult.Success(0.9421m, isFailureDetected: true);

        MethodInfo method = typeof(PrintFailureMonitorService).GetMethod(
            "HandleFailureDetectedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected HandleFailureDetectedAsync private method.");

        var task = (Task)(method.Invoke(service, [
            printer,
            "Obico",
            "http://camera.local/snapshot.jpg",
            result,
            dbContext,
            historyService,
            CancellationToken.None,
        ]) ?? throw new InvalidOperationException("Expected HandleFailureDetectedAsync invocation to return a task."));

        await task;

        FailureDetectionIncident incident = await dbContext.FailureDetectionIncidents.SingleAsync();
        incident.PrinterId.Should().Be(printer.Id);
        incident.JobId.Should().BeNull();
        incident.JobName.Should().Be("jobs/monitoring-job.gcode");
        incident.FileName.Should().Be("monitoring-job.gcode");
        incident.Confidence.Should().Be(0.9421m);
        incident.DetectedAt.Should().Be(result.AnalyzedAt);
        incident.SnapshotUrl.Should().Be("http://camera.local/snapshot.jpg");
        incident.AutoPaused.Should().BeFalse();

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "FailureDetected",
                It.Is<object[]>(args => HasExpectedFailureEvent(args, printer.Id)),
                It.IsAny<CancellationToken>()),
            Times.Once);

    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    public async Task RunMonitoringCycleAsync_ReportsProviderOutcomesAndPairingAccurately(
        bool captureSucceeds,
        bool captureReturnsJpeg,
        bool obicoUsesCapturedImage,
        bool obicoDetectsFailure)
    {
        Printer printer;
        byte[] imageData = captureReturnsJpeg ? [0xFF, 0xD8, 0xFF, 0xD9] : [0x00, 0x01, 0x02];
        bool captureHasValidImage = captureSucceeds && captureReturnsJpeg;
        string? expectedCaptureError = captureSucceeds
            ? captureReturnsJpeg ? null : "Captured camera image is not a JPEG."
            : "Camera capture unavailable.";
        string expectedAnalysisMode = captureHasValidImage && obicoUsesCapturedImage
            ? "paired-frame"
            : "unpaired";
        using (AppDbContext dbContext = CreateDbContext())
        {
            printer = await SeedPrinterAsync(dbContext, "Monitor Shadow Printer");
            printer.ObicoEnabled = true;
            printer.PrintGuardEnabled = true;
            Camera camera = new()
            {
                Id = Guid.NewGuid(),
                Name = "Monitor Shadow Camera",
                SnapshotUrl = "http://camera.local/snapshot.jpg",
                IsEnabled = true,
            };
            printer.CameraLinks.Add(new CameraPrinter
            {
                CameraId = camera.Id,
                Camera = camera,
                PrinterId = printer.Id,
                Printer = printer,
                IsPrimary = true,
            });
            _ = dbContext.Cameras.Add(camera);
            _ = dbContext.PrintJobs.Add(new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "monitor-shadow-job.gcode",
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Printing,
                Priority = 0,
                QueuePosition = 0,
                QueuedAt = DateTime.UtcNow.AddMinutes(-10),
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
            });
            await dbContext.SaveChangesAsync();
        }

        ObicoSettings obicoSettings = new()
        {
            Enabled = true,
            ObicoApiUrl = "http://obico.local",
            ScanIntervalSeconds = 30,
            ConfidenceThreshold = 0.8m,
            WarmupGracePeriodSeconds = 0,
        };
        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(service => service.Get<ObicoSettings>()).Returns(obicoSettings);

        DateTime obicoAnalyzedAt = DateTime.UtcNow;
        FailureDetectionProviderResult obicoResult = new(
            obicoDetectsFailure ? 0.9421m : 0.1m,
            obicoDetectsFailure,
            obicoAnalyzedAt,
            UsedCapturedImage: obicoUsesCapturedImage);
        FailureDetectionProviderResult healthyObicoResult = new(0.1m, false, DateTime.UtcNow);
        List<FailureDetectionProviderRequest> obicoRequests = [];
        Mock<IFailureDetectionProvider> obicoProvider = new();
        obicoProvider.SetupGet(provider => provider.ProviderName).Returns("Obico");
        obicoProvider
            .Setup(provider => provider.AnalyzeAsync(
                It.Is<FailureDetectionProviderRequest>(request =>
                    request.Endpoint == "http://obico.local"
                    && ((request.ImageData != null
                            && request.ImageData.SequenceEqual(imageData)
                            && request.SnapshotUrl == "http://camera.local/snapshot.jpg")
                        || (request.ImageData == null
                            && request.SnapshotUrl == "http://camera.local/snapshot.jpg"))),
                It.IsAny<CancellationToken>()))
            .Returns((FailureDetectionProviderRequest request, CancellationToken _) =>
            {
                obicoRequests.Add(request);
                return Task.FromResult(request.ImageData is null ? healthyObicoResult : obicoResult);
            });

        FailureDetectionProviderResult printGuardResult = new(0.1m, false, obicoAnalyzedAt.AddSeconds(1), UsedCapturedImage: true);
        Mock<IFailureDetectionProvider> printGuardProvider = new();
        printGuardProvider.SetupGet(provider => provider.ProviderName).Returns("PrintGuard");
        printGuardProvider
            .Setup(provider => provider.AnalyzeAsync(
                It.Is<FailureDetectionProviderRequest>(request =>
                    request.ImageData != null
                    && request.ImageData.SequenceEqual(imageData)
                    && request.Endpoint == "http://printguard.local/api/v1/classify"
                    && request.ApiToken == "shadow-token"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printGuardResult);

        Mock<ICameraSnapshotCaptureService> snapshotCapture = new();
        snapshotCapture
            .Setup(service => service.CaptureAsync(
                "http://camera.local/snapshot.jpg",
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(captureSucceeds
                ? CameraSnapshotCaptureResult.Succeeded(imageData)
                : CameraSnapshotCaptureResult.Failed("Camera capture unavailable."));

        Mock<IPrinterStatusCacheReader> statusCache = new();
        statusCache.Setup(cache => cache.GetStatus(printer.Id)).Returns(new PrinterStatusDto(
            printer.Id,
            true,
            "Printing",
            JobName: "monitor-shadow-job.gcode",
            FileName: "monitor-shadow-job.gcode"));

        Mock<IClientProxy> clientProxy = new();
        List<FailureDetectionDto> broadcastEvents = [];
        List<string> sideEffects = [];
        clientProxy
            .Setup(proxy => proxy.SendCoreAsync(
                "FailureDetected",
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
            {
                FailureDetectionDto failureEvent = (FailureDetectionDto)args[0];
                broadcastEvents.Add(failureEvent);
                sideEffects.Add($"broadcast:{failureEvent.Provider}");
            })
            .Returns(Task.CompletedTask);
        Mock<IHubClients> hubClients = new();
        hubClients.Setup(clients => clients.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.SetupGet(context => context.Clients).Returns(hubClients.Object);

        PrintGuardShadowSettings printGuardSettings = new()
        {
            Enabled = true,
            ApiUrl = "http://printguard.local",
            ApiToken = "shadow-token",
            RequestTimeoutSeconds = 7,
            CaptureTimeoutSeconds = 3,
        };
        Mock<IOptionsMonitor<PrintGuardShadowSettings>> optionsMonitor = new();
        optionsMonitor.SetupGet(options => options.CurrentValue).Returns(() => printGuardSettings);

        ServiceCollection services = new();
        _ = services.AddScoped(_ => CreateDbContext());
        _ = services.AddScoped<IFailureDetectionIncidentHistoryService, FailureDetectionIncidentHistoryService>();
        _ = services.AddSingleton<ISettingsService>(settingsService.Object);
        _ = services.AddSingleton<IOptionsMonitor<PrintGuardShadowSettings>>(optionsMonitor.Object);
        _ = services.AddSingleton<ICameraSnapshotCaptureService>(snapshotCapture.Object);
        _ = services.AddKeyedSingleton<IFailureDetectionProvider>("Obico", obicoProvider.Object);
        _ = services.AddKeyedSingleton<IFailureDetectionProvider>("PrintGuard", printGuardProvider.Object);

        using ServiceProvider rootProvider = services.BuildServiceProvider();
        FailureDetectionMonitorStatusStore monitorStatus = new();
        PrintFailureMonitorService monitor = new(
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            monitorStatus,
            statusCache.Object,
            hub.Object,
            new FailureDetectionMetrics(),
            Mock.Of<ILogger<PrintFailureMonitorService>>());

        MethodInfo method = typeof(PrintFailureMonitorService).GetMethod(
            "RunMonitoringCycleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected RunMonitoringCycleAsync private method.");
        var task = (Task)(method.Invoke(monitor, [CancellationToken.None])
            ?? throw new InvalidOperationException("Expected RunMonitoringCycleAsync invocation to return a task."));
        await task;

        snapshotCapture.Verify(
            service => service.CaptureAsync(
                "http://camera.local/snapshot.jpg",
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        obicoProvider.Verify(
            provider => provider.AnalyzeAsync(It.IsAny<FailureDetectionProviderRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        printGuardProvider.Verify(
            provider => provider.AnalyzeAsync(It.IsAny<FailureDetectionProviderRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(captureHasValidImage ? 1 : 0));
        broadcastEvents.Should().HaveCount(captureHasValidImage && obicoDetectsFailure ? 1 : 0);
        if (captureHasValidImage && obicoDetectsFailure)
        {
            FailureDetectionDto failureEvent = broadcastEvents.Should().ContainSingle().Which;
            failureEvent.Provider.Should().Be("Obico");
            failureEvent.AutoPaused.Should().BeFalse();
            sideEffects.Should().Equal("broadcast:Obico");
        }
        else
        {
            sideEffects.Should().BeEmpty();
        }

        using AppDbContext verificationContext = CreateDbContext();
        List<FailureDetectionIncident> incidents = await verificationContext.FailureDetectionIncidents.ToListAsync();
        incidents.Should().HaveCount(captureHasValidImage && obicoDetectsFailure ? 1 : 0);
        if (captureHasValidImage && obicoDetectsFailure)
        {
            FailureDetectionIncident incident = incidents.Should().ContainSingle().Which;
            incident.PrinterId.Should().Be(printer.Id);
            incident.Provider.Should().Be("Obico");
            incident.AutoPaused.Should().BeFalse();
        }

        FailureDetectionMonitorStatusDto snapshot = monitorStatus.GetSnapshot();
        snapshot.LastError.Should().BeNull();
        snapshot.LastFailureCount.Should().Be(captureHasValidImage && obicoDetectsFailure ? 1 : 0);
        snapshot.LastAnalyzedPrinterCount.Should().Be(1);
        FailureDetectionPrinterStatusDto printerStatus = snapshot.Printers.Should().ContainSingle().Which;
        printerStatus.State.Should().Be(captureHasValidImage ? "monitoring" : "error");
        printerStatus.LastOutcome.Should().Be(captureHasValidImage
            ? obicoDetectsFailure ? "failure" : "healthy"
            : "error");
        printerStatus.LastConfidence.Should().Be(captureHasValidImage && obicoDetectsFailure ? 0.9421m : 0.1m);
        printerStatus.DetectionSource.Should().Be("multiple");
        printerStatus.DetectionTarget.Should().Be("http://obico.local + PrintGuard");
        printerStatus.Providers.Should().HaveCount(2);
        printerStatus.Providers.Should().ContainSingle(provider =>
            provider.Provider == "Obico"
            && provider.LastOutcome == (captureHasValidImage && obicoDetectsFailure ? "failure" : "healthy")
            && provider.AnalysisMode == expectedAnalysisMode);
        printerStatus.Providers.Should().ContainSingle(provider =>
            provider.Provider == "PrintGuard"
            && provider.LastOutcome == (captureHasValidImage ? "healthy" : "error")
            && provider.Reason == (captureHasValidImage ? string.Empty : expectedCaptureError)
            && provider.AnalysisMode == expectedAnalysisMode);
        if (captureHasValidImage && !obicoDetectsFailure && obicoUsesCapturedImage)
        {
            printerStatus.Reason.Should().Be("Obico and PrintGuard analyzed the same captured frame.");
        }
        else if (captureHasValidImage && !obicoDetectsFailure)
        {
            printerStatus.Reason.Should().Be("Monitoring via Obico and PrintGuard.");
        }
        else
        {
            printerStatus.Reason.Should().NotContain("same captured frame");
        }

        printGuardSettings.Enabled = false;
        var nextTask = (Task)(method.Invoke(monitor, [CancellationToken.None])
            ?? throw new InvalidOperationException("Expected RunMonitoringCycleAsync invocation to return a task."));
        await nextTask;

        snapshot = monitorStatus.GetSnapshot();
        snapshot.LastError.Should().BeNull();
        snapshot.LastFailureCount.Should().Be(0);
        printerStatus = snapshot.Printers.Should().ContainSingle().Which;
        printerStatus.State.Should().Be("monitoring");
        printerStatus.LastOutcome.Should().Be("healthy");
        printerStatus.Providers.Should().ContainSingle(provider =>
            provider.Provider == "PrintGuard"
            && provider.State == "disabled"
            && provider.LastOutcome == (captureHasValidImage ? "healthy" : "error")
            && provider.Reason == "PrintGuard is disabled in deployment configuration. Set PFARM__PrintGuardShadow__Enabled=true and configure PFARM__PrintGuardShadow__ApiUrl.");
        snapshotCapture.Verify(
            service => service.CaptureAsync(
                "http://camera.local/snapshot.jpg",
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        obicoRequests.Should().HaveCount(2);
        if (captureHasValidImage)
        {
            obicoRequests[0].ImageData.Should().Equal(imageData);
        }
        else
        {
            obicoRequests[0].ImageData.Should().BeNull();
        }
        obicoRequests[0].SnapshotUrl.Should().Be("http://camera.local/snapshot.jpg");
        obicoRequests[1].ImageData.Should().BeNull();
        obicoRequests[1].SnapshotUrl.Should().Be("http://camera.local/snapshot.jpg");
    }

    [Theory]
    [InlineData(PrintJobStatus.Starting, true)]
    [InlineData(PrintJobStatus.Printing, true)]
    [InlineData(PrintJobStatus.Paused, false)]
    public async Task FindActiveJobAsync_OnlyReturnsRunningJobs(
        PrintJobStatus status,
        bool shouldReturnJob)
    {
        using AppDbContext dbContext = CreateDbContext();
        Printer printer = await SeedPrinterAsync(dbContext, $"Auto Pause {status} Printer");
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{status}-job",
            AssignedPrinterId = printer.Id,
            Status = status,
            Priority = 0,
            QueuePosition = 0,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
        };
        _ = dbContext.PrintJobs.Add(job);
        await dbContext.SaveChangesAsync();

        PrintJob? result = await PrintFailureMonitorService.FindActiveJobAsync(
            dbContext,
            printer.Id);

        if (shouldReturnJob)
        {
            result.Should().NotBeNull();
            result!.Id.Should().Be(job.Id);
        }
        else
        {
            result.Should().BeNull();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext() => new(_dbContextOptions);

    private static async Task<Printer> SeedPrinterAsync(AppDbContext dbContext, string printerName)
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{printerName} Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"{printerName} Model",
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = printerName,
            ServerUrl = $"http://{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };

        _ = dbContext.Manufacturers.Add(manufacturer);
        _ = dbContext.PrinterModels.Add(model);
        _ = dbContext.Printers.Add(printer);
        await dbContext.SaveChangesAsync();

        return printer;
    }

    private static bool HasExpectedFailureEvent(object[] args, Guid printerId)
    {
        if (args.Length != 1 || args[0] is not FailureDetectionDto dto)
        {
            return false;
        }

        return dto.PrinterId == printerId
            && dto.Id.HasValue
            && dto.JobName == "jobs/monitoring-job.gcode"
            && dto.FileName == "monitoring-job.gcode"
            && dto.AutoPaused == false;
    }
}
