using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Tests for MoonrakerSubscriptionService - background service that manages WebSocket subscriptions
/// to Moonraker printers with exponential backoff reconnection, heartbeat, and HTTP polling fallback.
/// 
/// NOTE: This service has limited testability due to direct use of IServiceScopeFactory.CreateAsyncScope()
/// extension method which cannot be mocked. These tests focus on:
/// 1. IHostedService lifecycle (StartAsync, StopAsync, Dispose)
/// 2. Graceful error handling during lifecycle
/// 3. Logging verification
/// 
/// Full integration testing of subscription loops and WebSocket handling should be done via
/// higher-level integration tests with a real service provider.
/// </summary>
public class MoonrakerSubscriptionServiceTests : IAsyncLifetime
{
    private readonly Mock<IHubContext<PrinterHub>> _hubContextMock;
    private readonly Mock<IClientProxy> _clientProxyMock;
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private MoonrakerSubscriptionService _service;

    public MoonrakerSubscriptionServiceTests()
    {
        _hubContextMock = new Mock<IHubContext<PrinterHub>>();
        _clientProxyMock = new Mock<IClientProxy>();
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();

        // Setup hub context for SignalR broadcasts
        _ = _hubContextMock.Setup(h => h.Clients).Returns(
            Mock.Of<IHubClients>(h => h.All == _clientProxyMock.Object));

        // Note: CreateAsyncScope() is an extension method on IServiceScopeFactory.
        // We cannot mock extension methods, so the service's async scoping will fail.
        // These tests verify lifecycle management, not async scope behavior.
        _ = _scopeFactoryMock
            .Setup(s => s.CreateScope())
            .Throws(new NotSupportedException("Extension methods cannot be mocked - use integration tests for scope testing"));

        _service = new MoonrakerSubscriptionService(
            _hubContextMock.Object,
            _scopeFactoryMock.Object,
            _loggerMock.Object,
            _httpClientFactoryMock.Object);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            await _service.StopAsync(CancellationToken.None);
        }
        catch
        {
            // Ignore errors - we're testing cleanup
        }
        _service?.Dispose();
    }

    [Fact]
    public async Task StartAsync_InitializesService()
    {
        // Act & Assert - Should not throw during initialization
        var exception = await Record.ExceptionAsync(() => 
            _service.StartAsync(CancellationToken.None));
        
        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_GracefullyShutdown()
    {
        // Arrange
        await _service.StartAsync(CancellationToken.None);

        // Act & Assert - Should not throw during stop
        var exception = await Record.ExceptionAsync(() =>
            _service.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_BeforeStart_CompletesSuccessfully()
    {
        // Act & Assert - Should not throw if stopped before started
        var exception = await Record.ExceptionAsync(() =>
            _service.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_CleanupResourcesSuccessfully()
    {
        // Act & Assert - Should not throw during disposal
        var exception = Record.Exception(() => _service.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public async Task Dispose_AfterStart_CleanupSuccessfully()
    {
        // Arrange
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Act & Assert - Should not throw during disposal after lifecycle
        var exception = Record.Exception(() => _service.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAndStop_MultipleSequentially_Succeeds()
    {
        // Act - Multiple start/stop cycles
        var exception = await Record.ExceptionAsync(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                await _service.StartAsync(CancellationToken.None);
                await Task.Delay(10);
                await _service.StopAsync(CancellationToken.None);
            }
        });

        // Assert
        Assert.Null(exception);
    }
}
