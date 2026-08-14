using System;
using System.Collections.Generic;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Unit tests for <see cref="BackendClientFactory"/> covering unsupported-backend handling
/// (issue #1545). These tests lock in that genuinely unsupported/invalid backend requests -
/// as opposed to the <see cref="PrinterBackend.Unknown"/> sentinel skipped during capability
/// discovery in <see cref="BackendCapabilityFactory"/> - continue to fail loudly with a
/// failure-level log and an explicit exception.
/// </summary>
public class BackendClientFactoryTests
{
    private static BackendClientFactory CreateFactory(Mock<ILogger<BackendClientFactory>> logger)
    {
        var registry = new Mock<IBackendPluginRegistry>();
        registry.Setup(r => r.GetAllExtendedPlugins()).Returns(Array.Empty<IExtendedBackendPlugin>());

        var serviceProvider = new Mock<IServiceProvider>();

        return new BackendClientFactory(serviceProvider.Object, registry.Object, logger.Object);
    }

    [Fact]
    public void GetClient_WithUnknownSentinelBackend_ThrowsArgumentExceptionAndLogsError()
    {
        // Arrange - no plugins registered, so no backend (including Unknown) is routable.
        var logger = new Mock<ILogger<BackendClientFactory>>();
        BackendClientFactory factory = CreateFactory(logger);

        // Act & Assert - PrinterBackend.Unknown requested directly through GetClient() (e.g. by
        // a caller that did not skip it) must still be rejected explicitly.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => factory.GetClient(PrinterBackend.Unknown));
        Assert.Contains("Unsupported printer backend", ex.Message, StringComparison.Ordinal);

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unsupported printer backend requested", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void GetClient_WithGenuinelyUnsupportedRealBackend_ThrowsArgumentExceptionAndLogsError()
    {
        // Arrange - no plugins registered, so a real (non-sentinel) backend like Moonraker is
        // also unroutable in this scenario, simulating a genuine misconfiguration.
        var logger = new Mock<ILogger<BackendClientFactory>>();
        BackendClientFactory factory = CreateFactory(logger);

        // Act & Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(() => factory.GetClient(PrinterBackend.Moonraker));
        Assert.Contains("Unsupported printer backend", ex.Message, StringComparison.Ordinal);

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unsupported printer backend requested", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void IsBackendSupported_ForUnknownSentinel_ReturnsFalseWithoutThrowing()
    {
        var logger = new Mock<ILogger<BackendClientFactory>>();
        BackendClientFactory factory = CreateFactory(logger);

        Assert.False(factory.IsBackendSupported(PrinterBackend.Unknown));
    }
}
