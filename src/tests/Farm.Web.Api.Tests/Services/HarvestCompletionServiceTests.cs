using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Gcode;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class HarvestCompletionServiceTests
{
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<IHarvestRepository> _harvestRepoMock;
    private readonly MockServiceProvider _mockServiceProvider;
    private readonly HarvestCompletionService _service;

    public HarvestCompletionServiceTests()
    {
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _harvestRepoMock = new Mock<IHarvestRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _unitOfWorkMock.Setup(u => u.HarvestOperations).Returns(_harvestRepoMock.Object);

        // Create a mock service scope that returns our mock service provider
        var serviceScopeMock = new Mock<IServiceScope>();
        serviceScopeMock.Setup(s => s.ServiceProvider)
            .Returns(new MockServiceProvider(_unitOfWorkMock.Object));
        serviceScopeMock.Setup(s => s.Dispose());

        Mock<IAsyncDisposable> asyncDisposableMock = serviceScopeMock.As<IAsyncDisposable>();
        asyncDisposableMock.Setup(s => s.DisposeAsync())
            .Returns(new ValueTask());

        // Create a real IServiceProvider wrapper that handles CreateAsyncScope
        _mockServiceProvider = new MockServiceProvider(_unitOfWorkMock.Object, serviceScopeMock.Object);

        _service = new HarvestCompletionService(_mockServiceProvider, _loggerMock.Object);
    }

    private class MockServiceProvider(IUnitOfWork unitOfWork, IServiceScope? scope = null) : IServiceProvider
    {
        private readonly IUnitOfWork _unitOfWork = unitOfWork;
        private readonly IServiceScope? _scope = scope;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IUnitOfWork))
            {
                return _unitOfWork;
            }
            // Return a mock scope factory that returns our scope
            return serviceType == typeof(IServiceScopeFactory) ? new MockServiceScopeFactory(_scope) : null;
        }
    }

    private class MockServiceScopeFactory(IServiceScope? scope) : IServiceScopeFactory
    {
        private readonly IServiceScope? _scope = scope;

        public IServiceScope CreateScope()
        {
            return _scope ?? throw new InvalidOperationException("No scope configured");
        }
    }

    private GcodeHarvestOperation CreateOperation(
        Guid id,
        int filesFound,
        int filesAdded,
        int filesSkipped,
        int filesErrored,
        GcodeHarvestStatus status = GcodeHarvestStatus.Running)
    {
        return new GcodeHarvestOperation
        {
            Id = id,
            FilesFound = filesFound,
            FilesAdded = filesAdded,
            FilesSkipped = filesSkipped,
            FilesErrored = filesErrored,
            Status = status,
            StartedAt = DateTime.UtcNow.AddHours(-1)
        };
    }

    [Fact]
    public async Task ExecuteAsync_WithNoRunningOperations_CompletesWithoutChanges()
    {
        // Arrange
        _harvestRepoMock.Setup(h => h.GetRunningOperationsWithFilesFoundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GcodeHarvestOperation>());

        // Act
        await _service.ProcessOperationsAsync(_unitOfWorkMock.Object, CancellationToken.None);

        // Assert
        _harvestRepoMock.Verify(h => h.GetRunningOperationsWithFilesFoundAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithCompleteOperation_MarksAsCompleted()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        GcodeHarvestOperation operation = CreateOperation(
            operationId,
            filesFound: 10,
            filesAdded: 8,
            filesSkipped: 2,
            filesErrored: 0);

        _harvestRepoMock.Setup(h => h.GetRunningOperationsWithFilesFoundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GcodeHarvestOperation> { operation });
        _harvestRepoMock.Setup(h => h.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        await _service.StartAsync(cts.Token);
        await Task.Delay(150);
        cts.Cancel();

        try
        {
            await _service.StopAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        // Assert
        Assert.Equal(GcodeHarvestStatus.Completed, operation.Status);
        Assert.NotNull(operation.CompletedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithIncompleteOperation_DoesNotMarkAsCompleted()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        GcodeHarvestOperation operation = CreateOperation(
            operationId,
            filesFound: 10,
            filesAdded: 5,
            filesSkipped: 2,
            filesErrored: 0); // Only 7 out of 10 processed

        _harvestRepoMock.Setup(h => h.GetRunningOperationsWithFilesFoundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GcodeHarvestOperation> { operation });

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        await _service.StartAsync(cts.Token);
        await Task.Delay(150);
        cts.Cancel();

        try
        {
            await _service.StopAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        // Assert
        Assert.NotEqual(GcodeHarvestStatus.Completed, operation.Status);
        _harvestRepoMock.Verify(h => h.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithPartialErrors_MarksCompleteIfAllProcessed()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        GcodeHarvestOperation operation = CreateOperation(
            operationId,
            filesFound: 10,
            filesAdded: 7,
            filesSkipped: 1,
            filesErrored: 2); // 10 total processed (7+1+2=10)

        _harvestRepoMock.Setup(h => h.GetRunningOperationsWithFilesFoundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GcodeHarvestOperation> { operation });

        // Act
        await _service.ProcessOperationsAsync(_unitOfWorkMock.Object, CancellationToken.None);

        // Assert
        Assert.Equal(GcodeHarvestStatus.Completed, operation.Status);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleOperations_ProcessesMostRecent()
    {
        // Arrange
        GcodeHarvestOperation op1 = CreateOperation(Guid.NewGuid(), 5, 5, 0, 0);
        GcodeHarvestOperation op2 = CreateOperation(Guid.NewGuid(), 10, 5, 3, 1); // Incomplete: 5+3+1=9 < 10

        _harvestRepoMock.Setup(h => h.GetRunningOperationsWithFilesFoundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GcodeHarvestOperation> { op1, op2 });
        _harvestRepoMock.Setup(h => h.GetDiscoveredFilesCountAsync(op1.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);
        _harvestRepoMock.Setup(h => h.GetDiscoveredFilesCountAsync(op2.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(9);
        _harvestRepoMock.Setup(h => h.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.ProcessOperationsAsync(_unitOfWorkMock.Object, CancellationToken.None);

        // Assert
        Assert.Equal(GcodeHarvestStatus.Completed, op1.Status);
        Assert.NotEqual(GcodeHarvestStatus.Completed, op2.Status);
    }

    [Fact]
    public async Task ExecuteAsync_OnCancellation_StopsGracefully()
    {
        // Arrange
        _harvestRepoMock.Setup(h => h.GetRunningOperationsWithFilesFoundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GcodeHarvestOperation>());

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)); // Increased to allow startup logging

        // Act
        await _service.StartAsync(cts.Token);
        await Task.Delay(100); // Give service time to start and log
        cts.Cancel();

        try
        {
            await _service.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        // Assert - Service should stop without throwing and log startup message
        _loggerMock.Verify(l => l.LogInformation(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<object?>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_OnException_LogsAndContinues()
    {
        // Arrange
        _harvestRepoMock.Setup(h => h.GetRunningOperationsWithFilesFoundAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        var logErrorSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loggerMock
            .Setup(l => l.LogError(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .Callback(() => logErrorSeen.TrySetResult());

        // Act
        await _service.StartAsync(CancellationToken.None);

        Task completed = await Task.WhenAny(
            logErrorSeen.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));

        try
        {
            await _service.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        Assert.Same(logErrorSeen.Task, completed);

        // Assert
        _loggerMock.Verify(l => l.LogError(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>()), Times.AtLeastOnce);
    }
}
