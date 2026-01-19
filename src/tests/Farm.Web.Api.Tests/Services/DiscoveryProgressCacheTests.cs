using Farm.Infrastructure;
using Farm.Infrastructure.Services;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services;

public class DiscoveryProgressCacheTests
{
    private readonly DiscoveryProgressCache _cache;

    public DiscoveryProgressCacheTests()
    {
        _cache = new DiscoveryProgressCache();
    }

    [Fact]
    public void Set_StoresProgressForSession()
    {
        var progress = new DiscoveryProgressDto(
            SessionId: "test-session",
            CurrentNetwork: "192.168.1.0/24",
            CurrentIp: "192.168.1.1",
            TotalIps: 10,
            ScannedIps: 5,
            PrintersFound: 2,
            PrintersExcluded: 0,
            ProgressPercentage: 50.0,
            Status: DiscoveryStatus.Scanning
        );

        _cache.Set("test-session", progress);

        bool found = _cache.TryGet("test-session", out DiscoveryProgressDto? retrieved);
        found.Should().BeTrue();
        retrieved.Should().NotBeNull();
        retrieved!.SessionId.Should().Be("test-session");
        retrieved.ScannedIps.Should().Be(5);
        retrieved.TotalIps.Should().Be(10);
    }

    [Fact]
    public void TryGet_ReturnsFalseForNonExistentSession()
    {
        bool found = _cache.TryGet("nonexistent", out DiscoveryProgressDto? progress);

        found.Should().BeFalse();
        progress.Should().BeNull();
    }

    [Fact]
    public void Remove_DeletesSessionData()
    {
        var progress = new DiscoveryProgressDto("test", "", "", 0, 0, 0, 0, 0, DiscoveryStatus.Scanning);
        _cache.Set("test", progress);

        _cache.Remove("test");

        bool found = _cache.TryGet("test", out _);
        found.Should().BeFalse();
    }

    [Fact]
    public void SetCancellationSource_StoresCancellationToken()
    {
        using var cts = new CancellationTokenSource();

        _cache.SetCancellationSource("session-1", cts);

        bool cancelled = _cache.TryCancel("session-1");
        cancelled.Should().BeTrue();
        cts.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void TryCancel_ReturnsFalseForNonExistentSession()
    {
        bool cancelled = _cache.TryCancel("nonexistent");

        cancelled.Should().BeFalse();
    }

    [Fact]
    public void TryCancel_ReturnsFalseForAlreadyCancelledSession()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _cache.SetCancellationSource("cancelled-session", cts);

        bool cancelled = _cache.TryCancel("cancelled-session");
        cancelled.Should().BeFalse();
    }

    [Fact]
    public void Remove_DisposesAndRemovesCancellationSource()
    {
        using var cts = new CancellationTokenSource();
        _cache.SetCancellationSource("session-dispose", cts);

        _cache.Remove("session-dispose");

        // After removal, trying to cancel should return false
        bool cancelled = _cache.TryCancel("session-dispose");
        cancelled.Should().BeFalse();
    }

    [Fact]
    public void SessionIdComparison_IsCaseInsensitive()
    {
        var progress = new DiscoveryProgressDto("Test-Session", "", "", 0, 0, 0, 0, 0, DiscoveryStatus.Scanning);
        _cache.Set("TEST-SESSION", progress);

        bool found = _cache.TryGet("test-session", out DiscoveryProgressDto? retrieved);
        found.Should().BeTrue();
        retrieved.Should().NotBeNull();
    }

    [Fact]
    public void Set_OverwritesExistingProgress()
    {
        var progress1 = new DiscoveryProgressDto("session", "", "", 0, 5, 0, 0, 0, DiscoveryStatus.Scanning);
        var progress2 = new DiscoveryProgressDto("session", "", "", 0, 10, 0, 0, 0, DiscoveryStatus.Scanning);

        _cache.Set("session", progress1);
        _cache.Set("session", progress2);

        _cache.TryGet("session", out DiscoveryProgressDto? retrieved);
        retrieved!.ScannedIps.Should().Be(10);
    }

    [Fact]
    public void MultipleSessions_MaintainedIndependently()
    {
        var progress1 = new DiscoveryProgressDto("session1", "", "", 0, 5, 0, 0, 0, DiscoveryStatus.Scanning);
        var progress2 = new DiscoveryProgressDto("session2", "", "", 0, 10, 0, 0, 0, DiscoveryStatus.Scanning);

        _cache.Set("session1", progress1);
        _cache.Set("session2", progress2);

        _cache.TryGet("session1", out DiscoveryProgressDto? retrieved1);
        _cache.TryGet("session2", out DiscoveryProgressDto? retrieved2);

        retrieved1!.ScannedIps.Should().Be(5);
        retrieved2!.ScannedIps.Should().Be(10);
    }

    [Fact]
    public void Remove_OnlyAffectsSpecifiedSession()
    {
        var progress1 = new DiscoveryProgressDto("session1", "", "", 0, 0, 0, 0, 0, DiscoveryStatus.Scanning);
        var progress2 = new DiscoveryProgressDto("session2", "", "", 0, 0, 0, 0, 0, DiscoveryStatus.Scanning);

        _cache.Set("session1", progress1);
        _cache.Set("session2", progress2);

        _cache.Remove("session1");

        _cache.TryGet("session1", out _).Should().BeFalse();
        _cache.TryGet("session2", out _).Should().BeTrue();
    }
}
