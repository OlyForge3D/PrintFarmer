using System;
using System.Net.WebSockets;
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
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Services.Sdcp;

/// <summary>
/// Unit tests for the "printerupdated" broadcast suppression gate wired into
/// <see cref="SdcpPollingService.PollPrinterAsync"/> (issue #1355). Mirrors
/// <c>PrusaLinkPollingServiceBroadcastGateTests</c>: <c>PollPrinterAsync</c> is a private,
/// infinitely-looping method, so tests invoke it via reflection and cancel the token from inside
/// the mocked <see cref="ISdcpClient.GetCompositeStatusAsync(string, CancellationToken)"/>
/// callback once the desired number of poll iterations has executed.
/// </summary>
public class SdcpPollingServiceBroadcastGateTests
{
    private readonly Mock<ISdcpClient> _sdcpClient = new(MockBehavior.Loose);
    private readonly Mock<IPrintersRepository> _printersRepository = new(MockBehavior.Loose);
    private readonly Mock<IUnitOfWork> _unitOfWork = new(MockBehavior.Loose);
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Loose);
    private readonly SdcpPollingService _service;
    private readonly MethodInfo _pollPrinterAsync;

    public SdcpPollingServiceBroadcastGateTests()
    {
        _unitOfWork.Setup(u => u.Printers).Returns(_printersRepository.Object);

        Mock<IServiceProvider> serviceProvider = new(MockBehavior.Loose);
        serviceProvider.Setup(p => p.GetService(typeof(ISdcpClient))).Returns(_sdcpClient.Object);
        serviceProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_unitOfWork.Object);

        Mock<IServiceScope> scope = new(MockBehavior.Loose);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new(MockBehavior.Loose);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _service = new SdcpPollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<SdcpPollingService>.Instance,
            statusCacheWriter: new Mock<IPrinterStatusCacheWriter>(MockBehavior.Loose).Object);

        _pollPrinterAsync = typeof(SdcpPollingService)
            .GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    private int SendCount() => _clientProxy.Invocations.Count(i => i.Method.Name == "SendCoreAsync");

    private static PrinterCompositeStatus IdleStatus() => new(
        IsOnline: true,
        State: "IDLE",
        Progress: null,
        JobName: null,
        ThumbnailUrl: null,
        CameraStreamUrl: null,
        CameraSnapshotUrl: null);

    [Fact]
    public async Task PollPrinterAsync_FirstPoll_IsNotSuppressed()
    {
        Guid printerId = Guid.NewGuid();
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = printerId, Name = "Test Printer", ServerUrl = "http://sdcp.local", Backend = (int)PrinterBackend.SDCP });

        using CancellationTokenSource cts = new();
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromResult(IdleStatus());
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation.
        }

        SendCount().Should().Be(1);
    }

    [Fact]
    public async Task PollPrinterAsync_ConsecutiveIdenticalPolls_SecondIsSuppressed()
    {
        Guid printerId = Guid.NewGuid();
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = printerId, Name = "Test Printer", ServerUrl = "http://sdcp.local", Backend = (int)PrinterBackend.SDCP });

        using CancellationTokenSource cts = new();
        int callIndex = 0;
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callIndex++;
                if (callIndex >= 2)
                {
                    cts.Cancel();
                }

                return Task.FromResult(IdleStatus());
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        SendCount().Should().Be(1, "a byte-identical repeat poll must be suppressed");
    }

    [Fact]
    public async Task PollPrinterAsync_WhenProgressChanges_BothPollsAreSent()
    {
        Guid printerId = Guid.NewGuid();
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = printerId, Name = "Test Printer", ServerUrl = "http://sdcp.local", Backend = (int)PrinterBackend.SDCP });

        PrinterCompositeStatus printing1 = IdleStatus() with { State = "PRINTING", Progress = 10, JobName = "benchy.gcode" };
        PrinterCompositeStatus printing2 = printing1 with { Progress = 15 };

        using CancellationTokenSource cts = new();
        int callIndex = 0;
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callIndex++;
                PrinterCompositeStatus status = callIndex == 1 ? printing1 : printing2;
                if (callIndex >= 2)
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
            // Expected.
        }

        SendCount().Should().Be(2, "a genuine progress change must never be suppressed");
    }

    [Fact]
    public async Task PollPrinterAsync_OfflineThenRecovered_RecoveryPollIsNotSuppressed()
    {
        Guid printerId = Guid.NewGuid();
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = printerId, Name = "Test Printer", ServerUrl = "http://sdcp.local", Backend = (int)PrinterBackend.SDCP });

        using CancellationTokenSource cts = new();
        int callIndex = 0;
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
                        throw new WebSocketException("Connection refused");
                    case 5:
                        cts.Cancel();
                        return Task.FromResult(IdleStatus());
                    default:
                        cts.Cancel();
                        throw new WebSocketException("unexpected extra call");
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
        // snapshot only in IsOnline/State.
        SendCount().Should().Be(3, "initial online + offline-after-3-failures + recovery must all be sent");
    }
}
