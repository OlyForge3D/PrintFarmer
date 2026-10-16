using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Cameras;

public sealed class PrinterCameraSyncServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IPrinterCameraSynchronizer> _synchronizer = new();
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly CameraSyncSettings _settings = new();
    private readonly ServiceProvider _provider;
    private readonly PrinterCameraSyncService _sut;
    private readonly List<Guid> _printerSyncs = [];
    private int _fullSyncs;
    private int _settingsReads;

    public PrinterCameraSyncServiceTests()
    {
        _settingsService
            .Setup(s => s.Get<CameraSyncSettings>())
            .Returns(() => _settings)
            .Callback(() => Interlocked.Increment(ref _settingsReads));
        _synchronizer
            .Setup(s => s.SyncAllAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref _fullSyncs))
            .ReturnsAsync([]);
        _synchronizer
            .Setup(s => s.SyncPrinterAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, bool _, CancellationToken _) =>
            {
                lock (_printerSyncs)
                {
                    _printerSyncs.Add(id);
                }
            })
            .ReturnsAsync((Guid id, bool _, CancellationToken _) => PrinterCameraSyncResult.NotFound(id));

        var services = new ServiceCollection();
        services.AddSingleton(_synchronizer.Object);
        services.AddSingleton(_settingsService.Object);
        _provider = services.BuildServiceProvider();
        _sut = new PrinterCameraSyncService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterCameraSyncService>.Instance,
            _time);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.StopAsync(CancellationToken.None);
        _sut.Dispose();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_BackfillsAllPrintersOnlyAfterStartupDelay()
    {
        await StartAndWaitForStartupDelayAsync();

        _time.Advance(StartupDelay - TimeSpan.FromSeconds(1));
        Volatile.Read(ref _fullSyncs).Should().Be(0);

        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Volatile.Read(ref _fullSyncs) == 1);
        _synchronizer.Verify(s => s.SyncPrinterAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestSync_SyncsRequestedPrinterAsImplicitRequest()
    {
        Guid printerId = Guid.NewGuid();
        await RunFirstFullSyncAsync();

        _sut.RequestSync(Guid.Empty);
        _sut.RequestSync(printerId);

        await WaitUntilAsync(() => PrinterSyncCount() == 1);
        _synchronizer.Verify(s => s.SyncPrinterAsync(printerId, false, It.IsAny<CancellationToken>()), Times.Once);
        _synchronizer.Verify(s => s.SyncPrinterAsync(Guid.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatsFullSyncOnConfiguredInterval()
    {
        _settings.IntervalMinutes = 5;
        await RunFirstFullSyncAsync();

        _time.Advance(TimeSpan.FromMinutes(4));
        Volatile.Read(ref _fullSyncs).Should().Be(1);

        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => Volatile.Read(ref _fullSyncs) == 2);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_SkipsFullAndTriggeredSyncs()
    {
        _settings.Enabled = false;
        await StartAndWaitForStartupDelayAsync();
        _time.Advance(StartupDelay);
        await _time.WaitForActiveTimersAsync(1, WaitTimeout);
        int readsBeforeRequest = Volatile.Read(ref _settingsReads);

        _sut.RequestSync(Guid.NewGuid());

        // The batch re-reads settings before deciding to skip, so a further read proves it was processed.
        await WaitUntilAsync(() => Volatile.Read(ref _settingsReads) > readsBeforeRequest);
        Volatile.Read(ref _fullSyncs).Should().Be(0);
        PrinterSyncCount().Should().Be(0);
    }

    private async Task StartAndWaitForStartupDelayAsync()
    {
        await _sut.StartAsync(CancellationToken.None);
        await _time.WaitForActiveTimersAsync(1, WaitTimeout);
    }

    private async Task RunFirstFullSyncAsync()
    {
        await StartAndWaitForStartupDelayAsync();
        _time.Advance(StartupDelay);
        await WaitUntilAsync(() => Volatile.Read(ref _fullSyncs) == 1);
        await _time.WaitForActiveTimersAsync(1, WaitTimeout);
    }

    private int PrinterSyncCount()
    {
        lock (_printerSyncs)
        {
            return _printerSyncs.Count;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cts.Token);
        }
    }
}
