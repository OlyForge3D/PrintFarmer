using System;
using System.Collections.Generic;
using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Contracts.Printers.OctoPrint;
using Farm.Infrastructure.Contracts.Printers.Sdcp;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Gcode;

/// <summary>
/// Integration tests for GcodeHarvestService file discovery with backend clients.
/// Tests the end-to-end flow of discovering files from Moonraker printers.
/// These tests verify that the capability factory returns clients that properly
/// implement the file listing interfaces expected by the harvest service.
/// </summary>
public class HarvestServiceFileDiscoveryIntegrationTests
{
    private readonly Mock<ILogger<BackendCapabilityFactory>> _mockLogger;
    private readonly Mock<IBackendClientFactory> _mockClientFactory;
    private readonly Mock<IBackendPluginRegistry> _mockPluginRegistry;

    public HarvestServiceFileDiscoveryIntegrationTests()
    {
        _mockLogger = new Mock<ILogger<BackendCapabilityFactory>>();
        _mockClientFactory = new Mock<IBackendClientFactory>();
        _mockPluginRegistry = new Mock<IBackendPluginRegistry>();
    }

    #region Tests for Backend Client Interface Implementation

    [Fact]
    public void BackendCapabilityFactory_TryGetFileListClient_ShouldReturnClientThatImplementsISupportsFileList()
    {
        // Arrange - Create a mock Moonraker client that implements ISupportsFileList
        var mockMoonrakerClient = new Mock<IMoonrakerClient>();
        mockMoonrakerClient.As<ISupportsFileList>()
            .Setup(c => c.GetFileListAsync(
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<PrinterFileInfo>
            {
                new()
                {
                    Name = "test.gcode",
                    Path = "/test.gcode",
                    Modified = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Size = 1024
                }
            });

        // IMoonrakerClient extends IBackendClient, so it's safe to return
        IBackendClient clientAsBackendClient = mockMoonrakerClient.As<IBackendClient>().Object;
        _mockClientFactory
            .Setup(f => f.GetClient(PrinterBackend.Moonraker))
            .Returns(clientAsBackendClient);

        // Set up mock plugin registry to indicate Moonraker supports file listing
        SetupMoonrakerPluginWithFileListCapability();
        var factory = new BackendCapabilityFactory(_mockClientFactory.Object, _mockLogger.Object, _mockPluginRegistry.Object);

        // Act
        bool result = factory.TryGetFileListClient(PrinterBackend.Moonraker, out IBackendClient? client);

        // Assert
        Assert.True(result, "Factory should successfully get file list client for Moonraker");
        Assert.NotNull(client);

        // CRITICAL: The returned client MUST implement ISupportsFileList
        // This is what was failing in the harvest service
        Assert.True(client is ISupportsFileList,
            $"Client returned from factory MUST implement ISupportsFileList interface. Got: {client?.GetType().FullName}");

        // Verify we can actually call the file list method
        var fileListClient = client as ISupportsFileList;
        Assert.NotNull(fileListClient);
    }

    [Fact]
    public void BackendCapabilityFactory_WithAllBackends_ShouldReturnProperlyTypedClients()
    {
        // Arrange - Set up mock clients for all backends that support file listing
        var moonrakerMock = new Mock<IMoonrakerClient>();
        moonrakerMock.As<ISupportsFileList>();
        IBackendClient moonrakerClient = moonrakerMock.As<IBackendClient>().Object;

        var prusaLinkMock = new Mock<IPrusaLinkClient>();
        prusaLinkMock.As<ISupportsFileList>();
        IBackendClient prusaLinkClient = prusaLinkMock.As<IBackendClient>().Object;

        var octoPrintMock = new Mock<IOctoPrintClient>();
        octoPrintMock.As<ISupportsFileList>();
        IBackendClient octoPrintClient = octoPrintMock.As<IBackendClient>().Object;

        var sdcpMock = new Mock<ISdcpClient>();
        sdcpMock.As<ISupportsFileList>();
        IBackendClient sdcpClient = sdcpMock.As<IBackendClient>().Object;

        _mockClientFactory
            .Setup(f => f.GetClient(PrinterBackend.Moonraker)).Returns(moonrakerClient);
        _mockClientFactory
            .Setup(f => f.GetClient(PrinterBackend.PrusaLink)).Returns(prusaLinkClient);
        _mockClientFactory
            .Setup(f => f.GetClient(PrinterBackend.OctoPrint)).Returns(octoPrintClient);
        _mockClientFactory
            .Setup(f => f.GetClient(PrinterBackend.SDCP)).Returns(sdcpClient);

        SetupAllPluginsWithFileListCapability();
        var factory = new BackendCapabilityFactory(_mockClientFactory.Object, _mockLogger.Object, _mockPluginRegistry.Object);

        // Act & Assert - Each backend should return a properly typed client
        AssertFileListClientSupported(factory, PrinterBackend.Moonraker);
        AssertFileListClientSupported(factory, PrinterBackend.PrusaLink);
        AssertFileListClientSupported(factory, PrinterBackend.OctoPrint);
        AssertFileListClientSupported(factory, PrinterBackend.SDCP);
    }

    #endregion

    #region Tests for Capability Detection Chain

    [Fact]
    public void CapabilityDetection_Chain_ShouldCorrectlyMapCapabilitiesToClients()
    {
        // Arrange - Trace the complete chain:
        // 1. Plugin declares FileList capability
        // 2. Factory detects capability
        // 3. Factory maps to BackendCapabilities.FileList
        // 4. TryGetFileListClient checks for FileList capability
        // 5. Returns properly typed client

        var mockMoonrakerClient = new Mock<IMoonrakerClient>();
        mockMoonrakerClient.As<ISupportsFileList>();
        IBackendClient clientAsBackendClient = mockMoonrakerClient.As<IBackendClient>().Object;

        _mockClientFactory
            .Setup(f => f.GetClient(PrinterBackend.Moonraker))
            .Returns(clientAsBackendClient);

        // Set up plugin to declare FileList capability
        var plugin = new Mock<IBackendClientPlugin>();
        plugin.Setup(p => p.BackendType).Returns("moonraker");
        plugin.Setup(p => p.GetCapabilities()).Returns(new[]
        {
            typeof(ISupportsFileList),
            typeof(ISupportsFileDownload),
            typeof(ISupportsControlOperations)
        });

        _mockPluginRegistry.Setup(r => r.IsRegistered("moonraker")).Returns(true);
        _mockPluginRegistry.Setup(r => r.GetPlugin("moonraker")).Returns(plugin.Object);

        // Act - Create factory and verify the chain
        var factory = new BackendCapabilityFactory(_mockClientFactory.Object, _mockLogger.Object, _mockPluginRegistry.Object);
        bool result = factory.TryGetFileListClient(PrinterBackend.Moonraker, out IBackendClient? returnedClient);

        // Assert
        Assert.True(result, "Capability chain should result in successful file list client retrieval");
        Assert.NotNull(returnedClient);
        Assert.IsAssignableFrom<ISupportsFileList>(returnedClient);
    }

    [Fact]
    public void MissingCapability_ShouldReturnFalse()
    {
        // Arrange - Plugin doesn't declare FileList capability
        var mockMoonrakerClient = new Mock<IMoonrakerClient>();
        // Note: NOT implementing ISupportsFileList
        IBackendClient clientAsBackendClient = mockMoonrakerClient.As<IBackendClient>().Object;

        _mockClientFactory
            .Setup(f => f.GetClient(PrinterBackend.Moonraker))
            .Returns(clientAsBackendClient);

        var plugin = new Mock<IBackendClientPlugin>();
        plugin.Setup(p => p.BackendType).Returns("moonraker");
        plugin.Setup(p => p.GetCapabilities()).Returns(new[]
        {
            typeof(ISupportsControlOperations),
            // FileList capability intentionally missing
        });

        _mockPluginRegistry.Setup(r => r.IsRegistered("moonraker")).Returns(true);
        _mockPluginRegistry.Setup(r => r.GetPlugin("moonraker")).Returns(plugin.Object);

        var factory = new BackendCapabilityFactory(_mockClientFactory.Object, _mockLogger.Object, _mockPluginRegistry.Object);

        // Act
        bool result = factory.TryGetFileListClient(PrinterBackend.Moonraker, out IBackendClient? client);

        // Assert
        Assert.False(result, "Should return false when capability is not declared");
        Assert.Null(client);
    }

    #endregion

    #region Helper Methods

    private void AssertFileListClientSupported(BackendCapabilityFactory factory, PrinterBackend backend)
    {
        bool result = factory.TryGetFileListClient(backend, out IBackendClient? client);
        Assert.True(result, $"{backend} should support file listing");
        Assert.NotNull(client);
        Assert.True(client is ISupportsFileList, $"{backend} client should implement ISupportsFileList");
    }

    private void SetupMoonrakerPluginWithFileListCapability()
    {
        var plugin = new Mock<IBackendClientPlugin>();
        plugin.Setup(p => p.BackendType).Returns("moonraker");
        plugin.Setup(p => p.GetCapabilities()).Returns(new[]
        {
            typeof(ISupportsFileList),
            typeof(ISupportsFileDownload),
            typeof(ISupportsStartPrint),
            typeof(ISupportsControlOperations),
            typeof(ISupportsCamera),
            typeof(ISupportsFileMetadata),
            typeof(ISupportsMovement),
            typeof(ISupportsTemperatureControl),
            typeof(ISupportsPrinterInformation)
        });

        _mockPluginRegistry.Setup(r => r.IsRegistered("moonraker")).Returns(true);
        _mockPluginRegistry.Setup(r => r.GetPlugin("moonraker")).Returns(plugin.Object);
    }

    private void SetupAllPluginsWithFileListCapability()
    {
        SetupPluginWithCapability("moonraker", new[]
        {
            typeof(ISupportsFileList),
            typeof(ISupportsFileDownload),
            typeof(ISupportsStartPrint),
            typeof(ISupportsControlOperations),
            typeof(ISupportsCamera),
            typeof(ISupportsFileMetadata),
            typeof(ISupportsMovement),
            typeof(ISupportsTemperatureControl),
            typeof(ISupportsPrinterInformation)
        });

        SetupPluginWithCapability("prusalink", new[]
        {
            typeof(ISupportsFileList),
            typeof(ISupportsFileDownload),
            typeof(ISupportsFileUpload),
            typeof(ISupportsStartPrint),
            typeof(ISupportsCamera),
            typeof(ISupportsPrinterInformation)
        });

        SetupPluginWithCapability("octoprint", new[]
        {
            typeof(ISupportsFileDownload),
            typeof(ISupportsFileList),
            typeof(ISupportsFileUpload),
            typeof(ISupportsCamera),
            typeof(ISupportsPrinterInformation)
        });

        SetupPluginWithCapability("sdcp", new[]
        {
            typeof(ISupportsFileList),
            typeof(ISupportsFileDownload),
            typeof(ISupportsControlOperations)
        });
    }

    private void SetupPluginWithCapability(string backendType, Type[] capabilities)
    {
        var plugin = new Mock<IBackendClientPlugin>();
        plugin.Setup(p => p.BackendType).Returns(backendType);
        plugin.Setup(p => p.GetCapabilities()).Returns(capabilities);

        _mockPluginRegistry.Setup(r => r.IsRegistered(backendType)).Returns(true);
        _mockPluginRegistry.Setup(r => r.GetPlugin(backendType)).Returns(plugin.Object);
    }

    #endregion
}
