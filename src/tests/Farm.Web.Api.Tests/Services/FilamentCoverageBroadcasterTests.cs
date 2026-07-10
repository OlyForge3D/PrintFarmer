using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Verifies the lowercase SignalR event contract for filament coverage
/// invalidations (issue #709). Wire name is <c>filamentcoveragechanged</c>
/// — must stay lowercase to match existing PrintFarmer conventions
/// (see <c>printerupdated</c>, <c>jobqueueupdate</c>). Payload shape and
/// reason vocabulary are pinned by Dallas's F4 addendum.
/// </summary>
public class FilamentCoverageBroadcasterTests
{
    [Fact]
    public async Task BroadcastPrinterChangedAsync_SendsLowercaseEvent_WithPrinterIdAndReasonPayload()
    {
        Guid printerId = Guid.NewGuid();

        Mock<IClientProxy> clientProxy = new(MockBehavior.Strict);
        clientProxy
            .Setup(c => c.SendCoreAsync(
                "filamentcoveragechanged",
                It.Is<object[]>(args =>
                    args.Length == 1
                    && args[0] is FilamentCoverageChangedEvent
                    && ((FilamentCoverageChangedEvent)args[0]).PrinterId == printerId
                    && ((FilamentCoverageChangedEvent)args[0]).Reason == FilamentCoverageChangeReasons.SpoolBinding
                    && ((FilamentCoverageChangedEvent)args[0]).OccurredAt != default),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        FilamentCoverageBroadcaster broadcaster = new(hub.Object, NullLogger<FilamentCoverageBroadcaster>.Instance);

        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.SpoolBinding, CancellationToken.None);

        clientProxy.Verify();
    }

    [Fact]
    public async Task BroadcastFleetChangedAsync_SendsLowercaseEvent_WithNullPrinterIdAndReason()
    {
        Mock<IClientProxy> clientProxy = new(MockBehavior.Strict);
        clientProxy
            .Setup(c => c.SendCoreAsync(
                "filamentcoveragechanged",
                It.Is<object[]>(args =>
                    args.Length == 1
                    && args[0] is FilamentCoverageChangedEvent
                    && ((FilamentCoverageChangedEvent)args[0]).PrinterId == null
                    && ((FilamentCoverageChangedEvent)args[0]).Reason == FilamentCoverageChangeReasons.ThresholdChanged),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        FilamentCoverageBroadcaster broadcaster = new(hub.Object, NullLogger<FilamentCoverageBroadcaster>.Instance);

        await broadcaster.BroadcastFleetChangedAsync(FilamentCoverageChangeReasons.ThresholdChanged, CancellationToken.None);

        clientProxy.Verify();
    }

    [Fact]
    public async Task Broadcast_EmptyReason_FallsBackToQueueChanged()
    {
        // Defensive: callers should never send an empty reason, but if they
        // do we must still emit a valid string on the wire so clients can
        // parse it. "queueChanged" is the most conservative refetch trigger.
        Mock<IClientProxy> clientProxy = new(MockBehavior.Strict);
        clientProxy
            .Setup(c => c.SendCoreAsync(
                "filamentcoveragechanged",
                It.Is<object[]>(args =>
                    args.Length == 1
                    && args[0] is FilamentCoverageChangedEvent
                    && ((FilamentCoverageChangedEvent)args[0]).Reason == FilamentCoverageChangeReasons.QueueChanged),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        FilamentCoverageBroadcaster broadcaster = new(hub.Object, NullLogger<FilamentCoverageBroadcaster>.Instance);

        await broadcaster.BroadcastPrinterChangedAsync(Guid.NewGuid(), string.Empty, CancellationToken.None);

        clientProxy.Verify();
    }

    [Fact]
    public void FilamentCoverageChangeReasons_ExposesExactlyTheDallasVocabulary()
    {
        // Pin the string vocabulary against the F4 addendum so a future
        // rename here is caught by a failing test.
        FilamentCoverageChangeReasons.JobProgress.Should().Be("jobProgress");
        FilamentCoverageChangeReasons.JobAssignment.Should().Be("jobAssignment");
        FilamentCoverageChangeReasons.QueueChanged.Should().Be("queueChanged");
        FilamentCoverageChangeReasons.SpoolBinding.Should().Be("spoolBinding");
        FilamentCoverageChangeReasons.SpoolWeight.Should().Be("spoolWeight");
        FilamentCoverageChangeReasons.ThresholdChanged.Should().Be("thresholdChanged");
    }
}
