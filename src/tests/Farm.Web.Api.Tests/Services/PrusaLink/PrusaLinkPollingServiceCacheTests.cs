using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure;
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

namespace Farm.Web.Api.Tests.Services.PrusaLink;

/// <summary>
/// Unit tests for the printer-row caching + invalidation behavior added to
/// <see cref="PrusaLinkPollingService"/> for issue #1763. Before this change, every 5s poll tick
/// re-opened a DI scope and re-queried (and re-decrypted) the printer row via
/// <c>GetPrinterAsync</c>. Now <c>PollPrinterAsync</c> only reads the row once - it is seeded by
/// the 30s reconciliation loop and cleared explicitly on printer edit - so steady-state polling
/// does zero per-tick DB reads for the printer row.
/// </summary>
public class PrusaLinkPollingServiceCacheTests
{
    private readonly Mock<IPrusaLinkClient> _prusaLinkClient = new(MockBehavior.Loose);
    private readonly Mock<IPrintersRepository> _printersRepository = new(MockBehavior.Loose);
    private readonly Mock<IUnitOfWork> _unitOfWork = new(MockBehavior.Loose);
    private readonly PrusaLinkPollingService _service;
    private readonly MethodInfo _pollPrinterAsync;
    private readonly MethodInfo _onPrinterInvalidated;
    private readonly FieldInfo _printerStatesField;
    private readonly Type _pollingStateType;

    public PrusaLinkPollingServiceCacheTests()
    {
        _unitOfWork.Setup(u => u.Printers).Returns(_printersRepository.Object);

        var spoolProvider = new ManagedSpoolProviderHelper(
            new Mock<ISpoolmanStatusCache>(MockBehavior.Loose).Object,
            NullLogger<ManagedSpoolProviderHelper>.Instance);

        Mock<IServiceProvider> serviceProvider = new(MockBehavior.Loose);
        serviceProvider.Setup(p => p.GetService(typeof(IPrusaLinkClient))).Returns(_prusaLinkClient.Object);
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

        _service = new PrusaLinkPollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<PrusaLinkPollingService>.Instance,
            statusCacheWriter: new Mock<IPrinterStatusCacheWriter>(MockBehavior.Loose).Object);

        Type serviceType = typeof(PrusaLinkPollingService);
        _pollPrinterAsync = serviceType.GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _onPrinterInvalidated = serviceType.GetMethod("OnPrinterInvalidated", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _printerStatesField = serviceType.GetField("_printerStates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _pollingStateType = serviceType.GetNestedType("PrinterPollingState", BindingFlags.NonPublic)!;
    }

    private static PrusaCompositeStatus IdleStatus() => new(
        IsOnline: true,
        State: "IDLE",
        Progress: null,
        JobName: null,
        ThumbnailUrl: null,
        CameraStreamUrl: null,
        CameraSnapshotUrl: null);

    /// <summary>
    /// Seeds <c>_printerStates</c> with a pre-populated <c>CachedPrinter</c>, mirroring what the
    /// 30s main loop does today - so tests can exercise <c>PollPrinterAsync</c>'s steady-state
    /// (cache-hit) path directly without needing to drive the real reconciliation loop.
    /// </summary>
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

    private async Task RunOneTickAsync(Guid printerId, PrusaCompositeStatus status)
    {
        using CancellationTokenSource cts = new();
        _prusaLinkClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
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
        var printer = new Printer { Id = printerId, Name = "Cached Printer", ServerUrl = "http://prusa.local", Backend = (int)PrinterBackend.PrusaLink };
        SeedCachedPrinter(printerId, printer);

        await RunOneTickAsync(printerId, IdleStatus());

        _printersRepository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "a seeded CachedPrinter must satisfy the poll tick without a fresh DB read");
    }

    [Fact]
    public async Task PollPrinterAsync_NoCachedPrinter_FallsBackToRepositoryAndPopulatesCache()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer { Id = printerId, Name = "Fallback Printer", ServerUrl = "http://prusa.local", Backend = (int)PrinterBackend.PrusaLink };
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
        var printer = new Printer { Id = printerId, Backend = (int)PrinterBackend.PrusaLink };
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

    [Fact]
    public async Task Invalidate_AfterCacheHit_ForcesExactlyOneRefetchThenResumesCacheHitBehavior()
    {
        Guid printerId = Guid.NewGuid();
        var original = new Printer { Id = printerId, Name = "Original", ServerUrl = "http://prusa.local", Backend = (int)PrinterBackend.PrusaLink };
        var updated = new Printer { Id = printerId, Name = "Updated", ServerUrl = "http://prusa-new.local", Backend = (int)PrinterBackend.PrusaLink };
        SeedCachedPrinter(printerId, original);

        // Simulate PrintersController.UpdateAsync calling Invalidate after a successful save.
        _onPrinterInvalidated.Invoke(_service, [printerId]);
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        await RunOneTickAsync(printerId, IdleStatus());

        _printersRepository.Verify(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once,
            "exactly one fresh read is expected immediately after invalidation");
        GetCachedPrinter(printerId).Should().BeSameAs(updated);

        // A subsequent tick must hit the now-repopulated cache again with no further DB reads.
        await RunOneTickAsync(printerId, IdleStatus());

        _printersRepository.Verify(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once,
            "the cache must be repopulated after the single post-invalidation refetch");
    }
}
