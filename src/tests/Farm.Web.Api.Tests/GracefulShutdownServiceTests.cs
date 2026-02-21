using Farm.Web.Api.Services.Startup;
using Farm.Web.Api.Tests.TestUtils;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Farm.Web.Api.Tests;

public class GracefulShutdownServiceTests
{
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IHostApplicationLifetime> _mockAppLifetime;
    private readonly TestLoggingService _testLogger;

    public GracefulShutdownServiceTests()
    {
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockAppLifetime = new Mock<IHostApplicationLifetime>();
        _testLogger = new TestLoggingService();
    }

    [Fact]
    public async Task StartAsync_RegistersApplicationStoppingCallbackAsync()
    {
        // Arrange
        GracefulShutdownService service = new GracefulShutdownService(
            _mockServiceProvider.Object,
            _mockAppLifetime.Object,
            _testLogger);
        CancellationToken cancellationToken = CancellationToken.None;
        _ = _mockAppLifetime.Setup(x => x.ApplicationStopping)
            .Returns(new CancellationToken());

        // Act
        await service.StartAsync(cancellationToken);

        // Assert
        _mockAppLifetime.Verify(x => x.ApplicationStopping, Times.Once);
    }

    [Fact]
    public async Task StopAsync_CompletesSuccessfullyAsync()
    {
        // Arrange
        GracefulShutdownService service = new GracefulShutdownService(
            _mockServiceProvider.Object,
            _mockAppLifetime.Object,
            _testLogger);
        CancellationToken cancellationToken = CancellationToken.None;

        // Act & Assert
        await service.StopAsync(cancellationToken); // Should complete without throwing
    }

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        // Arrange & Act
        GracefulShutdownService service = new GracefulShutdownService(
            _mockServiceProvider.Object,
            _mockAppLifetime.Object,
            _testLogger);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void Service_ImplementsIHostedService()
    {
        // Arrange
        GracefulShutdownService service = new GracefulShutdownService(
            _mockServiceProvider.Object,
            _mockAppLifetime.Object,
            _testLogger);

        // Assert
        Assert.True(service is IHostedService);
    }
}
