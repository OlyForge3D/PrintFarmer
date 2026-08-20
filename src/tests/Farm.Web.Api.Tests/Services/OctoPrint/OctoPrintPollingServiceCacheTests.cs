using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.OctoPrint;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OctoPrint;

/// <summary>
/// Unit tests for the printer-row caching + invalidation behavior added to
/// <see cref="OctoPrintPollingService"/> for issue #1763, plus the follow-up fix (PR #1786
/// review round) ensuring invalidation also tears down the long-lived
/// <see cref="OctoPrintWebSocketAdapter"/>. Unlike the other three backends, OctoPrint keeps a
/// persistent WebSocket connection alive between ticks; clearing only <c>CachedPrinter</c> would
/// leave that connection using the printer's old URL/API key for up to 30 seconds after an edit,
/// until the next reconciliation pass noticed the credential change. Invalidation must therefore
/// dispose the adapter immediately so the very next poll tick recreates it with fresh credentials.
/// </summary>
public class OctoPrintPollingServiceCacheTests
{
    private readonly Mock<IPrintersRepository> _printersRepository = new(MockBehavior.Loose);
    private readonly Mock<IOctoPrintClient> _octoPrintClient = new(MockBehavior.Loose);
    private readonly OctoPrintPollingService _service;
    private readonly MethodInfo _pollPrinterAsync;
    private readonly MethodInfo _onPrinterInvalidated;
    private readonly FieldInfo _printerStatesField;
    private readonly FieldInfo _webSocketAdaptersField;
    private readonly Type _pollingStateType;

    public OctoPrintPollingServiceCacheTests()
    {
        Mock<IServiceProvider> serviceProvider = new(MockBehavior.Loose);
        serviceProvider.Setup(p => p.GetService(typeof(IPrintersRepository))).Returns(_printersRepository.Object);
        serviceProvider.Setup(p => p.GetService(typeof(IOctoPrintClient))).Returns(_octoPrintClient.Object);

        Mock<IServiceScope> scope = new(MockBehavior.Loose);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new(MockBehavior.Loose);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IClientProxy> clientProxy = new(MockBehavior.Loose);
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _service = new OctoPrintPollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<OctoPrintPollingService>.Instance,
            statusCacheWriter: new Mock<IPrinterStatusCacheWriter>(MockBehavior.Loose).Object);

        Type serviceType = typeof(OctoPrintPollingService);
        _pollPrinterAsync = serviceType.GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _onPrinterInvalidated = serviceType.GetMethod("OnPrinterInvalidated", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _printerStatesField = serviceType.GetField("_printerStates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _webSocketAdaptersField = serviceType.GetField("_webSocketAdapters", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _pollingStateType = serviceType.GetNestedType("PrinterPollingState", BindingFlags.NonPublic)!;
    }

    private IDictionary PrinterStates => (IDictionary)_printerStatesField.GetValue(_service)!;

    private IDictionary WebSocketAdapters => (IDictionary)_webSocketAdaptersField.GetValue(_service)!;

    private OctoPrintWebSocketAdapter CreateFakeAdapter(Guid printerId, Printer printer) => new(
        printerId,
        printer,
        NullLogger.Instance,
        _octoPrintClient.Object,
        Mock.Of<IHubContext<PrinterHub>>(),
        Mock.Of<IPrinterStatusCacheWriter>());

    private void SeedState(Guid printerId, Printer? cachedPrinter, OctoPrintWebSocketAdapter? adapter)
    {
        object state = Activator.CreateInstance(_pollingStateType)!;
        _pollingStateType.GetProperty("PrinterId")!.SetValue(state, printerId);
        _pollingStateType.GetProperty("LastKnownIsOnline")!.SetValue(state, false);
        _pollingStateType.GetProperty("LastApiState")!.SetValue(state, "unset");
        _pollingStateType.GetProperty("CachedPrinter")!.SetValue(state, cachedPrinter);
        if (adapter != null)
        {
            _pollingStateType.GetProperty("CreatedWithServerUrl")!.SetValue(state, adapter is null ? null : cachedPrinter?.ServerUrl);
            _pollingStateType.GetProperty("CreatedWithApiKey")!.SetValue(state, cachedPrinter?.Credential?.ApiKey);
        }

        PrinterStates[printerId] = state;

        if (adapter != null)
        {
            WebSocketAdapters[printerId] = adapter;
        }
    }

    private Printer? GetCachedPrinter(Guid printerId)
    {
        object? state = PrinterStates[printerId];
        return state is null ? null : (Printer?)_pollingStateType.GetProperty("CachedPrinter")!.GetValue(state);
    }

    private async Task RunOneTickAsync(Guid printerId)
    {
        using CancellationTokenSource cts = new();
        _octoPrintClient
            .Setup(c => c.GetPrinterStateAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromResult<Farm.Infrastructure.Contracts.Printers.OctoPrint.OctoPrintPrinterState?>(null);
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation once the single tick completes.
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Expected: reflection wraps the OperationCanceledException thrown from the delay.
        }
    }

    [Fact]
    public void OnPrinterInvalidated_ExistingAdapter_DisposesAndRemovesAdapterAndClearsCache()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("old-key"),
        };
        OctoPrintWebSocketAdapter adapter = CreateFakeAdapter(printerId, printer);
        SeedState(printerId, printer, adapter);

        WebSocketAdapters.Contains(printerId).Should().BeTrue("the adapter must be seeded before invalidation");

        _onPrinterInvalidated.Invoke(_service, [printerId]);

        WebSocketAdapters.Contains(printerId).Should().BeFalse(
            "invalidation must tear down OctoPrint's persistent WebSocket adapter, not just clear CachedPrinter, " +
            "otherwise the stale connection keeps using the old URL/API key until the next 30s reconciliation pass");
        GetCachedPrinter(printerId).Should().BeNull("invalidation must force the very next poll to re-fetch fresh data");
    }

    [Fact]
    public void OnPrinterInvalidated_UnknownPrinterId_IsNoOp()
    {
        Action act = () => _onPrinterInvalidated.Invoke(_service, [Guid.NewGuid()]);

        act.Should().NotThrow("invalidation for a printer with no active polling state or adapter must be a safe no-op");
    }

    [Fact]
    public async Task PollPrinterAsync_AdapterMissingAfterInvalidation_RecreatesAdapterImmediately()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("new-key"),
        };

        // Simulate the state left behind by OnPrinterInvalidated: no adapter, no cached printer.
        SeedState(printerId, cachedPrinter: null, adapter: null);
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        WebSocketAdapters.Contains(printerId).Should().BeFalse();

        await RunOneTickAsync(printerId);

        WebSocketAdapters.Contains(printerId).Should().BeTrue(
            "PollPrinterAsync must recreate a missing WebSocket adapter on its very next tick instead of " +
            "waiting up to 30 seconds for the reconciliation loop, so an edited printer's new credentials " +
            "take effect immediately");
        GetCachedPrinter(printerId).Should().BeSameAs(printer);
    }
}
