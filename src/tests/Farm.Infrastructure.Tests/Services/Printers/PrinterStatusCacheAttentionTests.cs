using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Diagnostics;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

/// <summary>
/// Unit tests for the offline/online transition detection in <see cref="PrinterStatusCache"/>
/// (issue #707, review R3). Exactly one attention invalidation is emitted per boundary
/// crossing: <c>Created</c> on online→offline and <c>Resolved</c> on offline→online, keyed by
/// <c>offline:{printerId}</c>. No event is emitted when the online state is unchanged.
/// </summary>
public sealed class PrinterStatusCacheAttentionTests
{
    private readonly Mock<IAttentionBroadcaster> _broadcaster = new(MockBehavior.Loose);
    private readonly Mock<IDiagnosticChannelService> _diagnostics = new(MockBehavior.Loose);

    private PrinterStatusCache CreateCache()
    {
        _broadcaster.Setup(b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
        return new PrinterStatusCache(NullLogger<PrinterStatusCache>.Instance, _diagnostics.Object, _broadcaster.Object);
    }

    private static PrinterStatusDto Status(Guid id, bool online)
        => new(Id: id, IsOnline: online, State: online ? "Idle" : "Offline");

    [Fact]
    public void OnlineToOffline_EmitsSingleCreatedEvent()
    {
        Guid printer = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(Status(printer, online: true));

        cache.UpdateStatus(Status(printer, online: false));

        _broadcaster.Verify(
            b => b.NotifyChangedAsync(
                It.Is<AttentionChangedPayload>(p =>
                    p.ItemId == $"offline:{printer:D}" && p.ChangeKind == AttentionChangeKind.Created),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void OfflineToOnline_EmitsSingleResolvedEvent()
    {
        Guid printer = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        // Prime the cache as offline first (first offline frame is a no-op transition).
        cache.UpdateStatus(Status(printer, online: false));

        cache.UpdateStatus(Status(printer, online: true));

        _broadcaster.Verify(
            b => b.NotifyChangedAsync(
                It.Is<AttentionChangedPayload>(p =>
                    p.ItemId == $"offline:{printer:D}" && p.ChangeKind == AttentionChangeKind.Resolved),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void OfflineToOffline_EmitsNoEvent()
    {
        Guid printer = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(Status(printer, online: false));
        _broadcaster.Invocations.Clear();

        cache.UpdateStatus(Status(printer, online: false));

        _broadcaster.Verify(
            b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void OnlineToOnline_EmitsNoEvent()
    {
        Guid printer = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(Status(printer, online: true));
        _broadcaster.Invocations.Clear();

        cache.UpdateStatus(Status(printer, online: true));

        _broadcaster.Verify(
            b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
