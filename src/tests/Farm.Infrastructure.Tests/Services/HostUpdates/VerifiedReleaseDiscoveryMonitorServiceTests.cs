using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    private static (VerifiedReleaseDiscoveryMonitorService Service, IVerifiedReleaseEvidenceCache Cache, Mock<IBackgroundServiceMonitor> Monitor) CreateService(
        string channel,
        IHostUpdateMetadataProvider metadataProvider,
        IVerifiedReleaseManifestBindingStore? bindingStore = null)
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

        var optionsMonitor = new Mock<IOptionsMonitor<VerifiedReleaseDiscoveryOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue).Returns(new VerifiedReleaseDiscoveryOptions());

        var cache = new VerifiedReleaseEvidenceCache();
        var backgroundMonitor = new Mock<IBackgroundServiceMonitor>();

        var service = new VerifiedReleaseDiscoveryMonitorService(
            provider,
            NullLogger<VerifiedReleaseDiscoveryMonitorService>.Instance,
            optionsMonitor.Object,
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
            ComponentPlatformDigests: new Dictionary<string, string> { ["api/linux-x64"] = "sha256:" + new string('b', 64) },
            MinimumUpdaterVersion: "1.0.0");

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
            ComponentPlatformDigests: new Dictionary<string, string>(),
            MinimumUpdaterVersion: "1.0.0");

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
            ComponentPlatformDigests: new Dictionary<string, string>(),
            MinimumUpdaterVersion: "1.0.0");

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
    public async Task RunDiscoveryRoundAsync_InternalCancellation_ReportsOnceAndNextRoundRecovers()
    {
        SignedReleaseMetadata metadata = new(
            Channel: "stable",
            Sequence: 1,
            SignatureVerified: true,
            Identity: Identity("stable"),
            ComponentPlatformDigests: new Dictionary<string, string>(),
            MinimumUpdaterVersion: "1.0.0");
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
            ComponentPlatformDigests: new Dictionary<string, string>(),
            MinimumUpdaterVersion: "1.0.0");
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
}
