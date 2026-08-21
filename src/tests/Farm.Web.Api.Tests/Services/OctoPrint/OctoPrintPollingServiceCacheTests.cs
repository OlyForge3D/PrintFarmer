using System;
using System.Collections;
using System.Linq;
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
    private readonly MethodInfo _ensureWebSocketAdapter;
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
        _ensureWebSocketAdapter = serviceType.GetMethod("EnsureWebSocketAdapter", BindingFlags.NonPublic | BindingFlags.Instance)!;
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

    [Fact]
    public async Task PollPrinterAsync_InvalidationRacesInFlightCacheMissFetch_DoesNotPublishStaleData()
    {
        // Regression test for PR #1786 round 5 review (Hicks): PollPrinterAsync used to read
        // CachedPrinter, then separately re-read CacheGeneration *after* an async cache-miss fetch
        // completed, and unconditionally wrote the fetch result into CachedPrinter. An invalidation
        // that landed while the fetch's continuation was still pending (a real possibility -- an
        // awaited Task completing does not mean its continuation runs immediately) could therefore
        // have its cleared state silently overwritten by a fetch that started before the edit
        // committed, republishing the old row/adapter and undoing the invalidation until the next
        // 30s reconciliation pass.
        Guid printerId = Guid.NewGuid();
        var oldPrinter = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("old-key"),
        };

        // Cache miss: no cached printer, no adapter yet (generation starts at 0).
        SeedState(printerId, cachedPrinter: null, adapter: null);

        TaskCompletionSource fetchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Printer?> fetchResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                fetchStarted.TrySetResult();
                return fetchResult.Task;
            });

        using CancellationTokenSource cts = new();
        Task pollTask = (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;

        // Deterministically wait until PollPrinterAsync has started (and is awaiting) the
        // cache-miss fetch before simulating a concurrent edit + invalidation.
        await fetchStarted.Task;
        _onPrinterInvalidated.Invoke(_service, [printerId]);

        // Now let the in-flight fetch resolve with the row it captured *before* the invalidation
        // ran -- this is the stale data that must not be republished.
        fetchResult.SetResult(oldPrinter);

        // The tick must decline (generation fence mismatch) and skip via its Task.Delay; cancel to
        // break out of that delay immediately instead of waiting for the real polling interval.
        cts.Cancel();
        try
        {
            await pollTask;
        }
        catch (OperationCanceledException)
        {
            // Expected: the skipped-tick delay observes the cancellation.
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Expected: reflection wraps the OperationCanceledException thrown from the delay.
        }

        GetCachedPrinter(printerId).Should().BeNull(
            "a cache-miss fetch that raced an invalidation must not publish its stale result into " +
            "CachedPrinter -- otherwise a later tick could treat that stale row as current once the " +
            "generation stops moving");
        WebSocketAdapters.Contains(printerId).Should().BeFalse(
            "no WebSocket adapter should be constructed from a printer snapshot that predates the " +
            "invalidation that raced its fetch");
    }

    [Fact]
    public async Task EnsureWebSocketAdapter_ConcurrentCallers_OnlyOneAdapterIsConstructed()
    {
        // Regression test for PR #1786 review round 2 (Bishop/Hicks/Vasquez): the 30-second
        // reconciliation loop and PollPrinterAsync's per-tick "adapter missing" check both used to
        // call CreateWebSocketAdapter unconditionally, so a race after an invalidation could let both
        // construct a live WebSocket connection, with the loser's connection silently overwritten (and
        // never disposed) in _webSocketAdapters. EnsureWebSocketAdapter must serialize the
        // check-and-create so only one caller ever constructs an adapter for a given printer.
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("key"),
        };

        const int concurrency = 16;
        using Barrier barrier = new(concurrency);
        var tasks = new Task<OctoPrintWebSocketAdapter>[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return (OctoPrintWebSocketAdapter)_ensureWebSocketAdapter.Invoke(_service, [printerId, printer, CancellationToken.None, null])!;
            });
        }

        OctoPrintWebSocketAdapter[] results = await Task.WhenAll(tasks);

        results.Distinct().Should().ContainSingle(
            "concurrent recreation attempts for the same printer (e.g. the reconciliation loop racing " +
            "PollPrinterAsync's on-demand recreation after an invalidation) must serialize on a single " +
            "adapter construction so the loser doesn't silently overwrite (and leak) the winner's live " +
            "WebSocket connection");

        ((OctoPrintWebSocketAdapter)WebSocketAdapters[printerId]!).Should().BeSameAs(results[0]);
    }

    [Fact]
    public async Task EnsureWebSocketAdapter_ConcurrentCallersWithChangedCredentials_DisposesStaleAdapterExactlyOnceAndSharesReplacement()
    {
        // Regression test for the residual race identified during round-2 review: the reconciliation
        // loop's credential-changed teardown (TryRemove + Dispose) used to run outside any lock, so a
        // concurrent PollPrinterAsync tick's "is one missing?" check could still observe (and return)
        // the adapter that was about to be disposed. EnsureWebSocketAdapter folds the credential
        // comparison, stale-adapter disposal, and construction of the replacement into one atomic
        // per-printer critical section, so every concurrent caller either gets the single new adapter
        // and the stale one is disposed exactly once - never zero, never twice.
        Guid printerId = Guid.NewGuid();
        var staleCredentialPrinter = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("old-key"),
        };
        var updatedPrinter = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("new-key"),
        };

        OctoPrintWebSocketAdapter staleAdapter = CreateFakeAdapter(printerId, staleCredentialPrinter);
        SeedState(printerId, staleCredentialPrinter, staleAdapter);

        const int concurrency = 16;
        using Barrier barrier = new(concurrency);
        var tasks = new Task<OctoPrintWebSocketAdapter>[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return (OctoPrintWebSocketAdapter)_ensureWebSocketAdapter.Invoke(_service, [printerId, updatedPrinter, CancellationToken.None, null])!;
            });
        }

        OctoPrintWebSocketAdapter[] results = await Task.WhenAll(tasks);

        results.Distinct().Should().ContainSingle(
            "concurrent callers observing a credential change on the same printer must serialize on a " +
            "single replacement adapter, not each construct their own");
        results[0].Should().NotBeSameAs(staleAdapter, "the stale, old-credential adapter must be replaced, not reused");
        ((OctoPrintWebSocketAdapter)WebSocketAdapters[printerId]!).Should().BeSameAs(results[0]);
    }

    [Fact]
    public void EnsureWebSocketAdapter_CredentialsChanged_PreservesExistingPrinterPollingStateObject()
    {
        // Regression test for the round-3 review finding (Vasquez, Critical): the credential-changed
        // branch used to TryRemove the printer's entries from _pollingLoops and _printerStates, which
        // only dropped tracking references -- it never cancelled the already-running PollPrinterAsync
        // HTTP-fallback loop, which keeps looping forever on its own captured PrinterPollingState
        // object. The next reconciliation pass would then see "no polling loop" and spawn a *second*,
        // independent PollPrinterAsync task for the same printer -- a permanent per-edit leak of
        // background loops, with the orphaned original loop never observing future invalidations.
        // The fix must update the existing state object in place instead, so the one already-running
        // loop's captured reference sees the new adapter/credentials on its very next tick.
        Guid printerId = Guid.NewGuid();
        var staleCredentialPrinter = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("old-key"),
        };
        var updatedPrinter = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("new-key"),
        };

        OctoPrintWebSocketAdapter staleAdapter = CreateFakeAdapter(printerId, staleCredentialPrinter);
        SeedState(printerId, staleCredentialPrinter, staleAdapter);
        object originalStateObject = PrinterStates[printerId]!;

        _ensureWebSocketAdapter.Invoke(_service, [printerId, updatedPrinter, CancellationToken.None, null]);

        PrinterStates.Contains(printerId).Should().BeTrue(
            "a credential change must never remove the printer's tracking entry -- doing so orphans " +
            "the already-running PollPrinterAsync loop, which keeps polling on its own stale state " +
            "object forever while a second loop gets spawned for the same printer on the next " +
            "reconciliation pass");
        PrinterStates[printerId].Should().BeSameAs(
            originalStateObject,
            "the already-running polling loop's captured PrinterPollingState reference must be updated " +
            "in place (new adapter, new credentials) rather than replaced, so a single loop instance " +
            "continues to serve this printer across a credential change");
    }

    [Fact]
    public void EnsureWebSocketAdapter_StaleGenerationSnapshot_DeclinesToPublishFromStalePrinterData()
    {
        // Regression test for the round-4 review finding (Hicks, Warning): PollPrinterAsync used to
        // read state.CachedPrinter (and, before this fix, nothing about a generation) before it could
        // acquire the per-printer gate, then later call EnsureWebSocketAdapter with that snapshot. If
        // OnPrinterInvalidated ran in between -- clearing the cache and disposing the adapter under
        // the gate -- EnsureWebSocketAdapter would still happily reconstruct and publish a *new*
        // adapter built from the caller's now-stale printer argument, silently undoing the
        // invalidation until the next 30-second reconciliation pass corrected it. The generation fence
        // must make EnsureWebSocketAdapter decline to construct from a printer snapshot whose
        // generation no longer matches the current one.
        Guid printerId = Guid.NewGuid();
        var originalPrinter = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("original-key"),
        };

        // Seed state with an adapter so a generation of 0 is established, then simulate a poll tick
        // capturing `originalPrinter` and generation 0 *before* an invalidation runs.
        OctoPrintWebSocketAdapter originalAdapter = CreateFakeAdapter(printerId, originalPrinter);
        SeedState(printerId, originalPrinter, originalAdapter);
        long capturedGeneration = (long)_pollingStateType.GetProperty("CacheGeneration")!.GetValue(PrinterStates[printerId])!;

        // Now the printer is edited: invalidation runs (bumping the generation, clearing the cache,
        // and disposing/removing the adapter) *after* the caller above captured its stale snapshot.
        _onPrinterInvalidated.Invoke(_service, [printerId]);
        WebSocketAdapters.Contains(printerId).Should().BeFalse("invalidation must have torn down the adapter");

        // The caller now reaches EnsureWebSocketAdapter with its pre-invalidation snapshot and the
        // generation it captured beforehand -- which no longer matches the post-invalidation state.
        object? result = _ensureWebSocketAdapter.Invoke(_service, [printerId, originalPrinter, CancellationToken.None, capturedGeneration]);

        result.Should().BeNull(
            "a stale generation must make EnsureWebSocketAdapter decline to construct an adapter from " +
            "the caller's pre-invalidation printer snapshot -- publishing one would silently undo the " +
            "invalidation until the next reconciliation pass");
        WebSocketAdapters.Contains(printerId).Should().BeFalse(
            "no adapter must be published from the stale snapshot; the caller is expected to skip this " +
            "tick and re-resolve fresh data (and the current generation) before retrying");
        GetCachedPrinter(printerId).Should().BeNull(
            "the invalidation's cache clear must not be undone by the stale caller's snapshot");
    }

    [Fact]
    public void EnsureWebSocketAdapter_MatchingGenerationSnapshot_ConstructsNormally()
    {
        // Companion to the stale-generation test above: when the caller's captured generation still
        // matches (no invalidation raced it), EnsureWebSocketAdapter must behave exactly as before --
        // constructing and publishing a fresh adapter when none exists yet.
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("key"),
        };
        SeedState(printerId, printer, adapter: null);
        long capturedGeneration = (long)_pollingStateType.GetProperty("CacheGeneration")!.GetValue(PrinterStates[printerId])!;

        object? result = _ensureWebSocketAdapter.Invoke(_service, [printerId, printer, CancellationToken.None, capturedGeneration]);

        result.Should().NotBeNull("a matching generation must not block normal adapter construction");
        WebSocketAdapters.Contains(printerId).Should().BeTrue();
        ((OctoPrintWebSocketAdapter)WebSocketAdapters[printerId]!).Should().BeSameAs(result);
    }

    [Fact]
    public async Task OnPrinterInvalidated_ConcurrentWithEnsureWebSocketAdapter_NeverLeavesDisposedAdapterPublished()
    {
        // Regression test for the round-3 review finding (Hicks + Vasquez, Warning): OnPrinterInvalidated
        // used to mutate _printerStates/_webSocketAdapters without acquiring the same per-printer gate
        // that EnsureWebSocketAdapter uses, so the two could interleave -- e.g. invalidation could tear
        // down an adapter that EnsureWebSocketAdapter had just published (or was mid-construction) with
        // fresh credentials, or a concurrent PollPrinterAsync tick could observe/use an adapter the
        // instant after it was disposed by invalidation. Both now serialize on the same lock object per
        // printer id, so after any interleaving of concurrent calls, whatever adapter (if any) ends up
        // published in _webSocketAdapters must never be the disposed one.
        Guid printerId = Guid.NewGuid();
        var printer = new Printer
        {
            Id = printerId,
            Name = "OctoPrint",
            ServerUrl = "http://octo.local",
            Backend = (int)PrinterBackend.OctoPrint,
            Credential = PrinterCredential.FromApiKey("key"),
        };

        const int rounds = 200;
        for (int i = 0; i < rounds; i++)
        {
            var tasks = new Task[4];
            tasks[0] = Task.Run(() => _ensureWebSocketAdapter.Invoke(_service, [printerId, printer, CancellationToken.None, null]));
            tasks[1] = Task.Run(() => _onPrinterInvalidated.Invoke(_service, [printerId]));
            tasks[2] = Task.Run(() => _ensureWebSocketAdapter.Invoke(_service, [printerId, printer, CancellationToken.None, null]));
            tasks[3] = Task.Run(() => _onPrinterInvalidated.Invoke(_service, [printerId]));

            await Task.WhenAll(tasks);

            if (WebSocketAdapters[printerId] is OctoPrintWebSocketAdapter published)
            {
                bool isDisposed = (bool)typeof(OctoPrintWebSocketAdapter)
                    .GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(published)!;

                isDisposed.Should().BeFalse(
                    "whatever adapter ends up published in _webSocketAdapters after any interleaving of " +
                    "concurrent invalidation and (re)creation must be a live one -- a disposed adapter " +
                    "reaching the dictionary would mean the two operations raced instead of serializing " +
                    "on the shared per-printer gate");
            }
        }
    }
}
