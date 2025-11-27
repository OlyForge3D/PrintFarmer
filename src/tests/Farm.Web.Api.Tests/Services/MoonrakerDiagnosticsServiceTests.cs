using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
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
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        _ = mockClient.Setup(c => c.GetFileRootsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new FileRoot { Path = "/gcodes" } });

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        FileRoot[]? res = await svc.GetFileRootsAsync("http://example.local");

        Assert.NotNull(res);
        _ = Assert.Single(res!);
        Assert.Equal("/gcodes", res![0].Path);
    }

    [Fact]
    public async Task GetFileRootsAsync_ReturnsNull_WhenClientAlwaysThrows()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        _ = mockClient.Setup(c => c.GetFileRootsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        FileRoot[]? res = await svc.GetFileRootsAsync("http://example.local");

        Assert.Null(res);
    }

    [Fact]
    public async Task GetFileRootsAsync_RetriesUntilSuccess_OnThirdAttempt()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        _ = mockClient.SetupSequence(c => c.GetFileRootsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient1"))
            .ThrowsAsync(new InvalidOperationException("transient2"))
            .ReturnsAsync(new[] { new FileRoot { Path = "/gcodes" } });

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        FileRoot[]? res = await svc.GetFileRootsAsync("http://example.local");

        Assert.NotNull(res);
        _ = Assert.Single(res!);
    }

    [Fact]
    public async Task GetFileRootsAsync_LogsWarnings_ForEachRetry()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        _ = mockClient.SetupSequence(c => c.GetFileRootsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient1"))
            .ThrowsAsync(new InvalidOperationException("transient2"))
            .ReturnsAsync(new[] { new FileRoot { Path = "/gcodes" } });

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        FileRoot[]? res = await svc.GetFileRootsAsync("http://example.local");

        Assert.NotNull(res);
        // Verify LogWarning called at least twice (once per retry) - match existing overloads
        mockLogger.Verify(l => l.LogWarning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<object?>()), Times.AtLeast(2));
    }
}
