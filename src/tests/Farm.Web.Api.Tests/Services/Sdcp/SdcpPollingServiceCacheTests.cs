using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Sdcp;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Sdcp;

/// <summary>
/// Unit tests for the printer-row caching + invalidation behavior added to
/// <see cref="SdcpPollingService"/> for issue #1763, and the durable invalidation-generation
/// fence added afterward (PR #1786 review) to close a race where <c>PollPrinterAsync</c>'s
/// cache-miss fallback could publish a stale row if an invalidation arrived while the fetch was
/// still in flight.
/// </summary>
public class SdcpPollingServiceCacheTests
{
    private readonly Mock<ISdcpClient> _sdcpClient = new(MockBehavior.Loose);
    private readonly Mock<IPrintersRepository> _printersRepository = new(MockBehavior.Loose);
    private readonly Mock<IUnitOfWork> _unitOfWork = new(MockBehavior.Loose);
    private readonly SdcpPollingService _service;
    private readonly MethodInfo _pollPrinterAsync;
    private readonly MethodInfo _onPrinterInvalidated;
    private readonly FieldInfo _printerStatesField;
    private readonly Type _pollingStateType;

    public SdcpPollingServiceCacheTests()
    {
        _unitOfWork.Setup(u => u.Printers).Returns(_printersRepository.Object);

        var spoolProvider = new ManagedSpoolProviderHelper(
            new Mock<ISpoolmanStatusCache>(MockBehavior.Loose).Object,
            NullLogger<ManagedSpoolProviderHelper>.Instance);

        Mock<IServiceProvider> serviceProvider = new(MockBehavior.Loose);
        serviceProvider.Setup(p => p.GetService(typeof(ISdcpClient))).Returns(_sdcpClient.Object);
        serviceProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_unitOfWork.Object);
        serviceProvider.Setup(p => p.GetService(typeof(ManagedSpoolProviderHelper))).Returns(spoolProvider);

        Mock<IServiceScope> scope = new(MockBehavior.Loose);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new(MockBehavior.Loose);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IClientProxy> clientProxy = new(MockBehavior.Loose);
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _service = new SdcpPollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<SdcpPollingService>.Instance,
            statusCacheWriter: new Mock<IPrinterStatusCacheWriter>(MockBehavior.Loose).Object);

        Type serviceType = typeof(SdcpPollingService);
        _pollPrinterAsync = serviceType.GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _onPrinterInvalidated = serviceType.GetMethod("OnPrinterInvalidated", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _printerStatesField = serviceType.GetField("_printerStates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _pollingStateType = serviceType.GetNestedType("PrinterPollingState", BindingFlags.NonPublic)!;
    }

    private static PrinterCompositeStatus IdleStatus() => new(
        IsOnline: true,
        State: "IDLE",
        Progress: null,
        JobName: null,
        ThumbnailUrl: null,
        CameraStreamUrl: null,
        CameraSnapshotUrl: null);

    private void SeedCachedPrinter(Guid printerId, Printer printer)
    {
        var states = (System.Collections.IDictionary)_printerStatesField.GetValue(_service)!;
        object state = Activator.CreateInstance(_pollingStateType)!;
        _pollingStateType.GetProperty("PrinterId")!.SetValue(state, printerId);
        _pollingStateType.GetProperty("LastKnownIsOnline")!.SetValue(state, false);
        _pollingStateType.GetProperty("CachedPrinter")!.SetValue(state, printer);
        states[printerId] = state;
    }

    private Printer? GetCachedPrinter(Guid printerId)
    {
        var states = (System.Collections.IDictionary)_printerStatesField.GetValue(_service)!;
        object? state = states[printerId];
        return state is null ? null : (Printer?)_pollingStateType.GetProperty("CachedPrinter")!.GetValue(state);
    }

    private async Task RunOneTickAsync(Guid printerId, PrinterCompositeStatus status)
    {
        using CancellationTokenSource cts = new();
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromResult(status);
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation once the single tick completes.
        }
    }

    [Fact]
    public async Task PollPrinterAsync_CachedPrinterPresent_DoesNotQueryRepository()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer { Id = printerId, Name = "Cached Printer", ServerUrl = "192.168.1.60", Backend = (int)PrinterBackend.SDCP };
        SeedCachedPrinter(printerId, printer);

        await RunOneTickAsync(printerId, IdleStatus());

        _printersRepository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "a seeded CachedPrinter must satisfy the poll tick without a fresh DB read");
    }

    [Fact]
    public async Task PollPrinterAsync_NoCachedPrinter_FallsBackToRepositoryAndPopulatesCache()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer { Id = printerId, Name = "Fallback Printer", ServerUrl = "192.168.1.60", Backend = (int)PrinterBackend.SDCP };
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        await RunOneTickAsync(printerId, IdleStatus());

        _printersRepository.Verify(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        GetCachedPrinter(printerId).Should().BeSameAs(printer, "the cache-miss path must populate the cache for subsequent ticks");
    }

    [Fact]
    public void OnPrinterInvalidated_MatchingPrinterId_ClearsCachedPrinter()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer { Id = printerId, Backend = (int)PrinterBackend.SDCP };
        SeedCachedPrinter(printerId, printer);

        _onPrinterInvalidated.Invoke(_service, [printerId]);

        GetCachedPrinter(printerId).Should().BeNull("invalidation must force the very next poll to re-fetch fresh data");
    }

    [Fact]
    public void OnPrinterInvalidated_UnknownPrinterId_IsNoOp()
    {
        Action act = () => _onPrinterInvalidated.Invoke(_service, [Guid.NewGuid()]);

        act.Should().NotThrow("invalidation for a printer with no active polling state must be a safe no-op");
    }

    /// <summary>
    /// Regression test for the race flagged in PR #1786 review: <c>PollPrinterAsync</c>'s
    /// cache-miss fallback used to write back whatever <c>GetPrinterAsync</c> returned
    /// unconditionally, even if <see cref="IPrinterCacheInvalidator"/> invalidated the printer
    /// while that fetch was still in flight - resurrecting a stale row into the cache right after
    /// the edit that was supposed to clear it. The durable invalidation-generation fence must
    /// detect that race and decline to publish the stale result.
    /// </summary>
    [Fact]
    public async Task PollPrinterAsync_InvalidationRacesCacheMissFetch_DoesNotPublishStaleData()
    {
        Guid printerId = Guid.NewGuid();
        var stale = new Printer { Id = printerId, Name = "Stale", ServerUrl = "192.168.1.60", Backend = (int)PrinterBackend.SDCP };

        var fetchStarted = new TaskCompletionSource();
        var releaseFetch = new TaskCompletionSource();

        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                fetchStarted.SetResult();
                await releaseFetch.Task;
                return stale;
            });

        using CancellationTokenSource cts = new();
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromResult(IdleStatus());
            });

        Task pollTask = (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;

        await fetchStarted.Task;
        // Invalidate the printer while the cache-miss fetch above is still in flight.
        _onPrinterInvalidated.Invoke(_service, [printerId]);
        releaseFetch.SetResult();

        try
        {
            await pollTask;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation once the single tick completes.
        }

        GetCachedPrinter(printerId).Should().BeNull(
            "an invalidation racing the cache-miss fetch must prevent the stale row from being published");
    }
}
