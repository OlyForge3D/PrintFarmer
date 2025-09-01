using Farm.Web.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests;

public class GracefulShutdownServiceTests
{
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IHostApplicationLifetime> _mockAppLifetime;
    private readonly Mock<ILogger<GracefulShutdownService>> _mockLogger;

    public GracefulShutdownServiceTests()
    {
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockAppLifetime = new Mock<IHostApplicationLifetime>();
        _mockLogger = new Mock<ILogger<GracefulShutdownService>>();
    }

    [Fact]
    public async Task StartAsync_RegistersApplicationStoppingCallback()
    {
        // Arrange
        var service = new GracefulShutdownService(
            _mockServiceProvider.Object,
            _mockAppLifetime.Object,
            _mockLogger.Object);
        var cancellationToken = CancellationToken.None;
        _mockAppLifetime.Setup(x => x.ApplicationStopping)
            .Returns(new CancellationToken());

        // Act
        await service.StartAsync(cancellationToken);

        // Assert
        _mockAppLifetime.Verify(x => x.ApplicationStopping, Times.Once);
    }

    [Fact]
    public async Task StopAsync_CompletesSuccessfully()
    {
        // Arrange
        var service = new GracefulShutdownService(
            _mockServiceProvider.Object,
            _mockAppLifetime.Object,
            _mockLogger.Object);
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await service.StopAsync(cancellationToken); // Should complete without throwing
    }

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        // Arrange & Act
        var service = new GracefulShutdownService(
            _mockServiceProvider.Object,
            _mockAppLifetime.Object,
            _mockLogger.Object);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void Service_ImplementsIHostedService()
    {
        // Arrange
        var service = new GracefulShutdownService(
            _mockServiceProvider.Object,
            _mockAppLifetime.Object,
            _mockLogger.Object);

        // Assert
        Assert.True(service is IHostedService);
    }
}