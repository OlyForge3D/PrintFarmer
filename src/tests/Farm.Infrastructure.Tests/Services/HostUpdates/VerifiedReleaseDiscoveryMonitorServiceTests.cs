using System.Runtime.InteropServices;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage for <see cref="VerifiedReleaseDiscoveryMonitorService"/> (issue #2757 item
/// 3): exercises <see cref="VerifiedReleaseDiscoveryMonitorService.DiscoverAndCacheAsync"/>
/// directly (the internal test seam, mirroring <c>CatalogUpdateDetectionServiceTests</c>) rather
/// than the polling loop, so these tests run instantly regardless of the configured interval.
/// </summary>
public class VerifiedReleaseDiscoveryMonitorServiceTests
{
    private static CanonicalReleaseIdentity Identity(string channel) => new(
        ReleaseId: $"{channel}:2.0.0",
        Version: "2.0.0",
        Channel: channel,
        SourceTag: "v2.0.0",
        SourceBranch: "main",
        SourceCommit: new string('c', 40),
        AuthorizedBranchHead: new string('c', 40),
        BuildMetadata: "build-1",
        OciReleaseLabel: "v2.0.0",
        OciVersionLabel: "2.0.0",
        ManifestDigest: "sha256:" + new string('a', 64));

    private static string HostPlatform => $"{(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux")}-{RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "386",
        Architecture.Arm => "arm",
        _ => throw new PlatformNotSupportedException(),
    }}";

    private static IReadOnlyDictionary<string, string> PlatformDigests =>
        new Dictionary<string, string> { [$"api/{HostPlatform}"] = "sha256:" + new string('b', 64) };

    private static IReadOnlyDictionary<string, string> IndexDigests =>
        new Dictionary<string, string> { ["api"] = "sha256:" + new string('c', 64) };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ComponentPlatforms =>
        new Dictionary<string, IReadOnlyList<string>> { ["api"] = [HostPlatform] };

    private static (VerifiedReleaseDiscoveryMonitorService Service, IVerifiedReleaseEvidenceCache Cache, Mock<IBackgroundServiceMonitor> Monitor) CreateService(
        string channel,
        IHostUpdateMetadataProvider metadataProvider,
        IVerifiedReleaseManifestBindingStore? bindingStore = null,
        IOptionsMonitor<VerifiedReleaseDiscoveryOptions>? optionsMonitor = null,
        ILogger<VerifiedReleaseDiscoveryMonitorService>? logger = null)
    {
        var settingsService = new Mock<ISettingsService>();
        settingsService
            .Setup(s => s.Get<UpdateChannelSettings>())
            .Returns(new UpdateChannelSettings { Channel = channel, InsiderAcknowledged = channel == "insider" });

        var services = new ServiceCollection();
        services.AddSingleton(settingsService.Object);
        services.AddSingleton(metadataProvider);
        if (bindingStore is null)
        {
            var bindingStoreMock = new Mock<IVerifiedReleaseManifestBindingStore>();
            bindingStoreMock
                .Setup(store => store.EnsureBoundAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            bindingStore = bindingStoreMock.Object;
        }

        services.AddSingleton(bindingStore);
        ServiceProvider provider = services.BuildServiceProvider();

        IOptionsMonitor<VerifiedReleaseDiscoveryOptions> resolvedOptionsMonitor;
        if (optionsMonitor is null)
        {
            var defaultOptionsMonitor = new Mock<IOptionsMonitor<VerifiedReleaseDiscoveryOptions>>();
            defaultOptionsMonitor.Setup(m => m.CurrentValue).Returns(new VerifiedReleaseDiscoveryOptions());
            resolvedOptionsMonitor = defaultOptionsMonitor.Object;
        }
        else
        {
            resolvedOptionsMonitor = optionsMonitor;
        }

        var cache = new VerifiedReleaseEvidenceCache();
        var backgroundMonitor = new Mock<IBackgroundServiceMonitor>();

        var service = new VerifiedReleaseDiscoveryMonitorService(
            provider,
            logger ?? NullLogger<VerifiedReleaseDiscoveryMonitorService>.Instance,
            resolvedOptionsMonitor,
            backgroundMonitor.Object,
            cache);

        return (service, cache, backgroundMonitor);
    }

    [Fact]
    public async Task DiscoverAndCacheAsync_SuccessfulDiscovery_CachesVerifiedEvidence()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "stable",
            Sequence: 3,
            SignatureVerified: true,
            Identity: Identity("stable"),
            ComponentPlatformDigests: PlatformDigests,
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests,
            ComponentPlatforms: ComponentPlatforms);

        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.Setup(p => p.GetCurrentAsync("stable", It.IsAny<CancellationToken>())).ReturnsAsync(metadata);

        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, _) =
            CreateService("stable", provider.Object);

        await service.DiscoverAndCacheAsync(CancellationToken.None);

        cache.Current.Should().NotBeNull();
        cache.Current!.Identity!.Channel.Should().Be("stable");
        cache.LastVerifiedAt.Should().NotBeNull();
        cache.LastError.Should().BeNull();
    }

    [Fact]
    public async Task DiscoverAndCacheAsync_ReadsChannelFromPersistedSettings()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "insider",
            Sequence: 1,
            SignatureVerified: true,
            Identity: Identity("insider"),
            ComponentPlatformDigests: PlatformDigests,
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests,
            ComponentPlatforms: ComponentPlatforms);

        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.Setup(p => p.GetCurrentAsync("insider", It.IsAny<CancellationToken>())).ReturnsAsync(metadata);

        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, _) =
            CreateService("insider", provider.Object);

        await service.DiscoverAndCacheAsync(CancellationToken.None);

        provider.Verify(p => p.GetCurrentAsync("insider", It.IsAny<CancellationToken>()), Times.Once);
        cache.Current!.Identity!.Channel.Should().Be("insider");
    }

    [Fact]
    public async Task DiscoverAndCacheAsync_DiscoveryFailure_RecordsErrorAndRetainsPriorEvidence()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "stable",
            Sequence: 1,
            SignatureVerified: true,
            Identity: Identity("stable"),
            ComponentPlatformDigests: PlatformDigests,
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests,
            ComponentPlatforms: ComponentPlatforms);

        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.SetupSequence(p => p.GetCurrentAsync("stable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata)
            .ThrowsAsync(new InvalidDataException("no verified release found"));

        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, _) =
            CreateService("stable", provider.Object);

        await service.DiscoverAndCacheAsync(CancellationToken.None);
        VerifiedReleaseEvidenceDto? firstEvidence = cache.Current;
        DateTimeOffset? firstVerifiedAt = cache.LastVerifiedAt;

        Func<Task<bool>> secondRound = () => service.RunDiscoveryRoundAsync(3600, CancellationToken.None);

        (await secondRound()).Should().BeFalse();
        cache.Current.Should().BeSameAs(firstEvidence, "a discovery failure must not clear previously cached verified evidence");
        cache.LastVerifiedAt.Should().Be(firstVerifiedAt);
        cache.LastError.Should().Be("no verified release found");
    }

    [Fact]
    public async Task DiscoverAndCacheAsync_NoPriorEvidence_FailureLeavesCacheEmptyWithError()
    {
        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.Setup(p => p.GetCurrentAsync("stable", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("no releases found for channel"));

        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, _) =
            CreateService("stable", provider.Object);

        bool result = await service.RunDiscoveryRoundAsync(3600, CancellationToken.None);

        result.Should().BeFalse();
        cache.Current.Should().BeNull();
        cache.LastError.Should().Be("no releases found for channel");
    }

    [Fact]
    public async Task RunDiscoveryRoundAsync_Failure_LogsAndReportsExactlyOnce()
    {
        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.Setup(p => p.GetCurrentAsync("stable", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("release listing unavailable"));
        var logger = new CountingLogger();
        (VerifiedReleaseDiscoveryMonitorService service, _, Mock<IBackgroundServiceMonitor> monitor) =
            CreateService("stable", provider.Object, logger: logger);

        (await service.RunDiscoveryRoundAsync(3600, CancellationToken.None)).Should().BeFalse();

        logger.ErrorCount.Should().Be(1);
        monitor.Verify(
            item => item.ReportError(
                "VerifiedReleaseDiscoveryMonitorService",
                "release listing unavailable"),
            Times.Once);
    }

    [Fact]
    public async Task RunDiscoveryRoundAsync_SameChannelRollback_ReportsFailureAndRetainsNewerEvidence()
    {
        SignedReleaseMetadata sequence3 = new(
            Channel: "stable",
            Sequence: 3,
            SignatureVerified: true,
            Identity: Identity("stable"),
            ComponentPlatformDigests: PlatformDigests,
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests,
            ComponentPlatforms: ComponentPlatforms);
        SignedReleaseMetadata sequence2 = sequence3 with { Sequence = 2 };
        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.SetupSequence(p => p.GetCurrentAsync("stable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(sequence3)
            .ReturnsAsync(sequence2);
        var logger = new CountingLogger();
        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, Mock<IBackgroundServiceMonitor> monitor) =
            CreateService("stable", provider.Object, logger: logger);

        (await service.RunDiscoveryRoundAsync(3600, CancellationToken.None)).Should().BeTrue();
        VerifiedReleaseEvidenceCacheSnapshot accepted = cache.GetSnapshot();
        (await service.RunDiscoveryRoundAsync(3600, CancellationToken.None)).Should().BeFalse();

        const string rollbackMessage = "Rejected rollback release for channel 'stable' (sequence=2).";
        VerifiedReleaseEvidenceCacheSnapshot rejected = cache.GetSnapshot();
        rejected.Current.Should().BeSameAs(accepted.Current);
        rejected.Current!.Sequence.Should().Be(3);
        rejected.LastVerifiedAt.Should().Be(accepted.LastVerifiedAt);
        rejected.LastError.Should().Be(rollbackMessage);
        logger.ErrorCount.Should().Be(1);
        monitor.Verify(
            item => item.ReportError("VerifiedReleaseDiscoveryMonitorService", rollbackMessage),
            Times.Once);
    }

    [Fact]
    public async Task RunDiscoveryRoundAsync_InternalCancellation_ReportsOnceAndNextRoundRecovers()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "stable",
            Sequence: 1,
            SignatureVerified: true,
            Identity: Identity("stable"),
            ComponentPlatformDigests: PlatformDigests,
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests,
            ComponentPlatforms: ComponentPlatforms);
        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.SetupSequence(p => p.GetCurrentAsync("stable", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException())
            .ReturnsAsync(metadata);
        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, Mock<IBackgroundServiceMonitor> monitor) =
            CreateService("stable", provider.Object);

        (await service.RunDiscoveryRoundAsync(3600, CancellationToken.None)).Should().BeFalse();
        (await service.RunDiscoveryRoundAsync(3600, CancellationToken.None)).Should().BeTrue();

        monitor.Verify(m => m.ReportError(
            "VerifiedReleaseDiscoveryMonitorService",
            It.IsAny<string>()), Times.Once);
        monitor.Verify(m => m.ReportSuccess(
            "VerifiedReleaseDiscoveryMonitorService",
            3600), Times.Once);
        cache.Current.Should().NotBeNull();
        cache.LastError.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_OptionsReloadValidationFailure_ReportsAndDelaysBeforeNextIteration()
    {
        var provider = new Mock<IHostUpdateMetadataProvider>(MockBehavior.Strict);
        var optionsMonitor = new Mock<IOptionsMonitor<VerifiedReleaseDiscoveryOptions>>();
        var validationFailure = new OptionsValidationException(
            Options.DefaultName,
            typeof(VerifiedReleaseDiscoveryOptions),
            ["invalid interval"]);
        optionsMonitor.Setup(m => m.CurrentValue).Throws(validationFailure);
        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, Mock<IBackgroundServiceMonitor> monitor) =
            CreateService("stable", provider.Object, optionsMonitor: optionsMonitor.Object);
        TaskCompletionSource delayObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? observedDelay = null;
        service.DelayAsync = (delay, cancellationToken) =>
        {
            observedDelay = delay;
            delayObserved.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        await service.StartAsync(CancellationToken.None);
        await delayObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        observedDelay.Should().Be(TimeSpan.FromSeconds(new VerifiedReleaseDiscoveryOptions().IntervalSeconds));
        cache.Current.Should().BeNull();
        cache.LastError.Should().Contain("invalid interval");
        provider.Verify(
            p => p.GetCurrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        optionsMonitor.Verify(m => m.CurrentValue, Times.Once);
        monitor.Verify(m => m.ReportError(
            "VerifiedReleaseDiscoveryMonitorService",
            It.Is<string>(message => message.Contains("invalid interval", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task RunDiscoveryRoundAsync_ShutdownCancellation_PropagatesWithoutFailureReport()
    {
        using CancellationTokenSource stopping = new();
        stopping.Cancel();
        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.Setup(p => p.GetCurrentAsync("stable", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(stopping.Token));
        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, Mock<IBackgroundServiceMonitor> monitor) =
            CreateService("stable", provider.Object);

        Func<Task> act = async () => await service.RunDiscoveryRoundAsync(3600, stopping.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        monitor.Verify(m => m.ReportError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        cache.LastError.Should().BeNull();
    }

    [Fact]
    public async Task DiscoverAndCacheAsync_ChangedDigestBinding_RejectsBeforeCacheReplacement()
    {
        SignedReleaseMetadata first = new(
            Channel: "stable",
            Sequence: 1,
            SignatureVerified: true,
            Identity: Identity("stable"),
            ComponentPlatformDigests: PlatformDigests,
            MinimumUpdaterVersion: "1.0.0",
            ComponentIndexDigests: IndexDigests,
            ComponentPlatforms: ComponentPlatforms);
        SignedReleaseMetadata changed = first with
        {
            Identity = first.Identity with { ManifestDigest = "sha256:" + new string('f', 64) },
        };
        var provider = new Mock<IHostUpdateMetadataProvider>();
        provider.SetupSequence(p => p.GetCurrentAsync("stable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(first)
            .ReturnsAsync(changed);
        var bindingStore = new Mock<IVerifiedReleaseManifestBindingStore>();
        bindingStore.Setup(store => store.EnsureBoundAsync(
                first.Identity.ReleaseId,
                first.Identity.ManifestDigest,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        bindingStore.Setup(store => store.EnsureBoundAsync(
                changed.Identity.ReleaseId,
                changed.Identity.ManifestDigest,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("manifest digest conflict"));
        (VerifiedReleaseDiscoveryMonitorService service, IVerifiedReleaseEvidenceCache cache, _) =
            CreateService("stable", provider.Object, bindingStore.Object);

        await service.DiscoverAndCacheAsync(CancellationToken.None);
        VerifiedReleaseEvidenceDto accepted = cache.Current!;
        (await service.RunDiscoveryRoundAsync(3600, CancellationToken.None)).Should().BeFalse();

        cache.Current.Should().BeSameAs(accepted);
        cache.Current!.ManifestDigest.Should().Be(first.Identity.ManifestDigest);
    }

    private sealed class CountingLogger : ILogger<VerifiedReleaseDiscoveryMonitorService>
    {
        public int ErrorCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                ErrorCount++;
            }
        }
    }
}
