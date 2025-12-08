using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
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

    [Fact]
    public async Task GetDirectoryAsync_ReturnsDirectoryInfo_OnSuccess()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        var dirInfo = new MoonrakerDirectoryInfo 
        { 
            Path = "/gcodes",
            Dirname = "gcodes",
            Modified = 1700000000,
            Size = 4096,
            Dirs = new[] { new MoonrakerDirectoryInfo { Dirname = "subfolder1" }, new MoonrakerDirectoryInfo { Dirname = "subfolder2" } },
            Files = new[] { new MoonrakerFileInfo { Path = "file1.gcode", Size = 1024 }, new MoonrakerFileInfo { Path = "file2.gcode", Size = 2048 } }
        };

        _ = mockClient.Setup(c => c.GetDirectoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dirInfo);

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        MoonrakerDirectoryInfo? res = await svc.GetDirectoryAsync("http://example.local", "gcodes");

        Assert.NotNull(res);
        Assert.Equal("gcodes", res!.Dirname);
        Assert.Equal(2, res.Dirs.Length);
        Assert.Equal(2, res.Files.Length);
    }

    [Fact]
    public async Task GetDirectoryAsync_ReturnsNull_WhenServiceFails()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        _ = mockClient.Setup(c => c.GetDirectoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection failed"));

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        MoonrakerDirectoryInfo? res = await svc.GetDirectoryAsync("http://example.local");

        Assert.Null(res);
    }

    [Fact]
    public async Task GetDirectoryAsync_RetriesUntilSuccess()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        var dirInfo = new MoonrakerDirectoryInfo 
        { 
            Path = "/gcodes",
            Dirname = "gcodes"
        };

        _ = mockClient.SetupSequence(c => c.GetDirectoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("timeout"))
            .ThrowsAsync(new InvalidOperationException("timeout"))
            .ReturnsAsync(dirInfo);

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        MoonrakerDirectoryInfo? res = await svc.GetDirectoryAsync("http://example.local");

        Assert.NotNull(res);
        Assert.Equal("gcodes", res!.Dirname);
    }

    [Fact]
    public async Task GetDetailedFileListAsync_ReturnsFileList_OnSuccess()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        var fileInfos = new[]
        {
            new MoonrakerFileInfo { Path = "model1.gcode", Size = 1024, Modified = 1700000000 },
            new MoonrakerFileInfo { Path = "model2.gcode", Size = 2048, Modified = 1700000100 }
        };

        _ = mockClient.Setup(c => c.GetDetailedFileListAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fileInfos);

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        MoonrakerFileInfo[]? res = await svc.GetDetailedFileListAsync("http://example.local");

        Assert.NotNull(res);
        Assert.Equal(2, res!.Length);
        Assert.Contains(res, f => f.Path == "model1.gcode");
        Assert.Contains(res, f => f.Path == "model2.gcode");
    }

    [Fact]
    public async Task GetDetailedFileListAsync_ReturnsNull_WhenServiceFails()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        _ = mockClient.Setup(c => c.GetDetailedFileListAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("directory not found"));

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        MoonrakerFileInfo[]? res = await svc.GetDetailedFileListAsync("http://example.local");

        Assert.Null(res);
    }

    [Fact]
    public async Task GetDetailedFileListAsync_WithSubdirectory_ReturnsFilteredList()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        var fileInfos = new[]
        {
            new MoonrakerFileInfo { Path = "subdir/model1.gcode", Size = 1024, Modified = 1700000000 },
            new MoonrakerFileInfo { Path = "subdir/model2.gcode", Size = 2048, Modified = 1700000100 }
        };

        _ = mockClient.Setup(c => c.GetDetailedFileListAsync(It.IsAny<string>(), It.IsAny<string>(), "subdir", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fileInfos);

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        MoonrakerFileInfo[]? res = await svc.GetDetailedFileListAsync("http://example.local", "gcodes", "subdir");

        Assert.NotNull(res);
        Assert.Equal(2, res!.Length);
        Assert.All(res, f => Assert.Contains("subdir", f.Path));
    }

    [Fact]
    public async Task GetDetailedFileListAsync_HandlesEmptyDirectory()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        _ = mockClient.Setup(c => c.GetDetailedFileListAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MoonrakerFileInfo>());

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        MoonrakerFileInfo[]? res = await svc.GetDetailedFileListAsync("http://example.local");

        Assert.NotNull(res);
        Assert.Empty(res!);
    }

    [Fact]
    public async Task GetDetailedFileListAsync_RetriesUntilSuccess()
    {
        Mock<IMoonrakerClient> mockClient = new Mock<IMoonrakerClient>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

        var fileInfos = new[]
        {
            new MoonrakerFileInfo { Path = "model1.gcode", Size = 1024, Modified = 1700000000 }
        };

        _ = mockClient.SetupSequence(c => c.GetDetailedFileListAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network timeout"))
            .ReturnsAsync(fileInfos);

        MoonrakerDiagnosticsService svc = new MoonrakerDiagnosticsService(mockClient.Object, mockLogger.Object);

        MoonrakerFileInfo[]? res = await svc.GetDetailedFileListAsync("http://example.local");

        Assert.NotNull(res);
        Assert.Single(res!);
    }
}
