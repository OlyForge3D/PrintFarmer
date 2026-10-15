using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Diagnostics;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Printers;

/// <summary>
/// Camera discovery is triggered when a printer connects (#3328/#3335): the status cache must
/// request exactly one camera sync per offline→online transition and none otherwise.
/// </summary>
public sealed class PrinterStatusCacheCameraSyncTests
{
    private readonly Mock<IPrinterCameraSyncTrigger> _trigger = new();

    private PrinterStatusCache CreateCache() =>
        new(NullLogger<PrinterStatusCache>.Instance, Mock.Of<IDiagnosticChannelService>(), attentionBroadcaster: null, _trigger.Object);

    private static PrinterStatusDto Status(Guid id, bool online)
        => new(Id: id, IsOnline: online, State: online ? "Idle" : "Offline");

    [Fact]
    public void FirstOnlineFrame_RequestsCameraSyncOnce()
    {
        Guid printer = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();

        cache.UpdateStatus(Status(printer, online: true));
        cache.UpdateStatus(Status(printer, online: true));

        _trigger.Verify(t => t.RequestSync(printer), Times.Once);
        _trigger.VerifyNoOtherCalls();
    }

    [Fact]
    public void GoingOffline_DoesNotRequestCameraSync()
    {
        Guid printer = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(Status(printer, online: true));
        _trigger.Invocations.Clear();

        cache.UpdateStatus(Status(printer, online: false));
        cache.UpdateStatus(Status(printer, online: false));

        _trigger.VerifyNoOtherCalls();
    }

    [Fact]
    public void Reconnect_RequestsCameraSyncAgain()
    {
        Guid printer = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(Status(printer, online: true));
        cache.UpdateStatus(Status(printer, online: false));

        cache.UpdateStatus(Status(printer, online: true));

        _trigger.Verify(t => t.RequestSync(printer), Times.Exactly(2));
    }

    [Fact]
    public void BatchUpdate_RequestsCameraSyncOnlyForPrintersThatCameOnline()
    {
        Guid connecting = Guid.NewGuid();
        Guid stillOnline = Guid.NewGuid();
        Guid stillOffline = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatuses([Status(connecting, false), Status(stillOnline, true), Status(stillOffline, false)]);
        _trigger.Invocations.Clear();

        cache.UpdateStatuses([Status(connecting, true), Status(stillOnline, true), Status(stillOffline, false)]);

        _trigger.Verify(t => t.RequestSync(connecting), Times.Once);
        _trigger.VerifyNoOtherCalls();
    }
}
