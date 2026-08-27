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

namespace Farm.Backend.Plugins.Tests.Services.PrusaLink;

/// <summary>
/// Unit tests for the "printerupdated" broadcast suppression gate wired into
/// <see cref="PrusaLinkPollingService.PollPrinterAsync"/> (issue #1355). <c>PollPrinterAsync</c>
/// is a private, infinitely-looping method, so tests invoke it via reflection and cancel the
/// token from inside the mocked <see cref="IPrusaLinkClient.GetCompositeStatusAsync"/> callback
/// once the desired number of poll iterations has executed. Cancelling there lets the current
/// iteration finish (compare + optionally broadcast + cache update) before the trailing
/// <c>await Task.Delay(PollingInterval, ct)</c> throws and exits the loop. Multi-iteration tests
/// incur the real (5s) production polling interval between iterations, since the interval is a
/// <c>private static readonly</c> field that cannot be shrunk via reflection once the type has
/// initialized elsewhere in the test run.
/// </summary>
public class PrusaLinkPollingServiceBroadcastGateTests
{
    private readonly Mock<IPrusaLinkClient> _prusaLinkClient = new(MockBehavior.Loose);
    private readonly Mock<IPrintersRepository> _printersRepository = new(MockBehavior.Loose);
    private readonly Mock<IUnitOfWork> _unitOfWork = new(MockBehavior.Loose);
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Loose);
    private readonly PrusaLinkPollingService _service;
    private readonly MethodInfo _pollPrinterAsync;

    public PrusaLinkPollingServiceBroadcastGateTests()
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

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _service = new PrusaLinkPollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<PrusaLinkPollingService>.Instance,
            statusCacheWriter: new Mock<IPrinterStatusCacheWriter>(MockBehavior.Loose).Object);

        _pollPrinterAsync = typeof(PrusaLinkPollingService)
            .GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    private int SendCount() => _clientProxy.Invocations.Count(i => i.Method.Name == "SendCoreAsync");

    private static PrusaCompositeStatus IdleStatus() => new(
        IsOnline: true,
        State: "IDLE",
        Progress: null,
        JobName: null,
        ThumbnailUrl: null,
        CameraStreamUrl: null,
        CameraSnapshotUrl: null);

    /// <summary>
    /// Runs PollPrinterAsync, returning statuses from <paramref name="statuses"/> in order (one
    /// per poll iteration), cancelling the token once the last status has been returned so the
    /// loop exits cleanly after processing exactly <c>statuses.Length</c> iterations.
    /// </summary>
    private async Task RunPollLoopAsync(Guid printerId, params PrusaCompositeStatus[] statuses)
    {
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = printerId, Name = "Test Printer", ServerUrl = "http://prusa.local", Backend = (int)PrinterBackend.PrusaLink });

        using CancellationTokenSource cts = new();
        int callIndex = 0;
        _prusaLinkClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                PrusaCompositeStatus status = statuses[callIndex];
                callIndex++;
                if (callIndex >= statuses.Length)
                {
                    cts.Cancel();
                }

                return Task.FromResult(status);
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation once all statuses are consumed.
        }
    }

    [Fact]
    public async Task PollPrinterAsync_FirstPoll_IsNotSuppressed()
    {
        Guid printerId = Guid.NewGuid();

        await RunPollLoopAsync(printerId, IdleStatus());

        SendCount().Should().Be(1);
    }

    [Fact]
    public async Task PollPrinterAsync_ConsecutiveIdenticalPolls_SecondIsSuppressed()
    {
        Guid printerId = Guid.NewGuid();
        PrusaCompositeStatus idle = IdleStatus();

        await RunPollLoopAsync(printerId, idle, idle with { });

        SendCount().Should().Be(1, "a byte-identical repeat poll must be suppressed");
    }

    [Fact]
    public async Task PollPrinterAsync_WhenProgressChanges_BothPollsAreSent()
    {
        Guid printerId = Guid.NewGuid();
        PrusaCompositeStatus printing1 = IdleStatus() with { State = "PRINTING", Progress = 10, JobName = "benchy.gcode" };
        PrusaCompositeStatus printing2 = printing1 with { Progress = 15 };

        await RunPollLoopAsync(printerId, printing1, printing2);

        SendCount().Should().Be(2, "a genuine progress change must never be suppressed");
    }

    [Fact]
    public async Task PollPrinterAsync_OfflineThenRecovered_RecoveryPollIsNotSuppressed()
    {
        Guid printerId = Guid.NewGuid();

        // First poll succeeds (online), establishing LastBroadcastUpdate.
        // Second-through-fourth polls throw to simulate 3 consecutive failures (the offline
        // branch requires state.ConsecutiveFailures >= 3), which triggers the offline broadcast
        // on the 3rd failure (call 4). The fifth poll succeeds again (recovered) -> must not be
        // suppressed even though the offline broadcast was the most recent cached update.
        Guid pid = printerId;
        _printersRepository
            .Setup(r => r.FindByIdAsync(pid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = pid, Name = "Test Printer", ServerUrl = "http://prusa.local", Backend = (int)PrinterBackend.PrusaLink });

        using CancellationTokenSource cts = new();
        int callIndex = 0;
        _prusaLinkClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callIndex++;
                switch (callIndex)
                {
                    case 1:
                        return Task.FromResult(IdleStatus());
                    case 2:
                    case 3:
                    case 4:
                        throw new HttpRequestException("Connection refused");
                    case 5:
                        cts.Cancel();
                        return Task.FromResult(IdleStatus());
                    default:
                        cts.Cancel();
                        throw new HttpRequestException("unexpected extra call");
                }
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        // Poll 1 (online, first message): sent.
        // Polls 2-4 (failures): no printerupdated send until the 3rd consecutive failure (poll 4)
        // marks the printer offline and broadcasts that.
        // Poll 5 (recovered): must be sent, not suppressed, despite differing from the offline
        // snapshot only in IsOnline/State — which is exactly the point of the gate.
        SendCount().Should().Be(3, "initial online + offline-after-3-failures + recovery must all be sent");
    }
}
