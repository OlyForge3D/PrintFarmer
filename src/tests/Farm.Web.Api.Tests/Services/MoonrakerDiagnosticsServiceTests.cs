using System.Threading.Tasks;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class MoonrakerDiagnosticsServiceTests
{
    [Fact]
    public async Task GetFileRootsAsync_ReturnsRoots_WhenClientSucceeds()
    {
        var mockClient = new Mock<IMoonrakerClient>();
        var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

        mockClient.Setup(c => c.GetFileRootsAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new[] { new FileRoot { Path = "/gcodes" } });

        var svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        var res = await svc.GetFileRootsAsync("http://example.local");

        Assert.NotNull(res);
        Assert.Single(res!);
        Assert.Equal("/gcodes", res![0].Path);
    }

    [Fact]
    public async Task GetFileRootsAsync_ReturnsNull_WhenClientAlwaysThrows()
    {
        var mockClient = new Mock<IMoonrakerClient>();
        var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

        mockClient.Setup(c => c.GetFileRootsAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new System.Exception("boom"));

        var svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        var res = await svc.GetFileRootsAsync("http://example.local");

        Assert.Null(res);
    }

    [Fact]
    public async Task GetFileRootsAsync_RetriesUntilSuccess_OnThirdAttempt()
    {
        var mockClient = new Mock<IMoonrakerClient>();
        var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

        mockClient.SetupSequence(c => c.GetFileRootsAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new System.InvalidOperationException("transient1"))
            .ThrowsAsync(new System.InvalidOperationException("transient2"))
            .ReturnsAsync(new[] { new FileRoot { Path = "/gcodes" } });

        var svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        var res = await svc.GetFileRootsAsync("http://example.local");

        Assert.NotNull(res);
        Assert.Single(res!);
    }

    [Fact]
    public async Task GetFileRootsAsync_LogsWarnings_ForEachRetry()
    {
        var mockClient = new Mock<IMoonrakerClient>();
        var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

        mockClient.SetupSequence(c => c.GetFileRootsAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new System.InvalidOperationException("transient1"))
            .ThrowsAsync(new System.InvalidOperationException("transient2"))
            .ReturnsAsync(new[] { new FileRoot { Path = "/gcodes" } });

        var svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        var res = await svc.GetFileRootsAsync("http://example.local");

        Assert.NotNull(res);
        // Verify LogWarning called at least twice (once per retry)
        mockLogger.Verify(l => l.LogWarning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<object?>()), Times.AtLeast(2));
    }
}
