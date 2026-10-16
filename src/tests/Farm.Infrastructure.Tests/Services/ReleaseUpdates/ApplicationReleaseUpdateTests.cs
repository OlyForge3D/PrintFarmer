using System.Net;
using System.Text;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.ReleaseUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Listing = Farm.Infrastructure.Services.ReleaseUpdates.GitHubApplicationReleaseSource.GitHubReleaseListing;

namespace Farm.Infrastructure.Tests.Services.ReleaseUpdates;

public sealed class ApplicationReleaseUpdateTests
{
    [Theory]
    [InlineData("0.2.3", "0.2.3", ApplicationReleaseChannel.Stable)]
    [InlineData("v0.2.3", "0.2.3", ApplicationReleaseChannel.Stable)]
    [InlineData("0.2.3-insider.5", "0.2.3-insider.5", ApplicationReleaseChannel.Insider)]
    [InlineData(" v10.0.12-insider.17 ", "10.0.12-insider.17", ApplicationReleaseChannel.Insider)]
    public void TryParse_AcceptsReleaseIdentifiers(string input, string expected, ApplicationReleaseChannel channel)
    {
        ApplicationReleaseVersion.TryParse(input, out ApplicationReleaseVersion? version).Should().BeTrue();
        version!.Version.Should().Be(expected);
        version.Tag.Should().Be("v" + expected);
        version.Channel.Should().Be(channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("development")]
    [InlineData("v1.0-beta.3")]
    [InlineData("ios/v1.0-beta.1")]
    [InlineData("0.2.3-insider.0")]
    [InlineData("0.2.3-beta.1")]
    [InlineData("01.2.3")]
    [InlineData("0.2.3+abcdef")]
    [InlineData("vv0.2.3")]
    [InlineData("99999999999.0.0")]
    public void TryParse_RejectsNonReleaseIdentifiers(string? input)
    {
        ApplicationReleaseVersion.TryParse(input, out ApplicationReleaseVersion? version).Should().BeFalse();
        version.Should().BeNull();
    }

    [Theory]
    [InlineData("0.2.3", "0.2.4")]
    [InlineData("0.2.9", "0.10.0")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("0.2.3-insider.5", "0.2.3")]
    [InlineData("0.2.3-insider.9", "0.2.3-insider.10")]
    [InlineData("0.2.3", "0.2.4-insider.1")]
    public void Ordering_MatchesReleasePolicy(string lower, string higher)
    {
        ApplicationReleaseVersion low = Parse(lower);
        ApplicationReleaseVersion high = Parse(higher);

        (low < high).Should().BeTrue();
        (high > low).Should().BeTrue();
        low.CompareTo(high).Should().BeNegative();
        high.CompareTo(low).Should().BePositive();
        Parse(lower).CompareTo(low).Should().Be(0);
    }

    [Fact]
    public void SelectLatest_IgnoresDraftsUnrelatedTagsAndOtherChannel()
    {
        Listing[] releases =
        [
            new("v0.2.3-insider.5", "Insider 5", false, true, null),
            new("v0.2.3-insider.9", "Draft insider", true, true, null),
            new("v0.2.3-insider.7", "Mislabelled", false, false, null),
            new("ios/v1.0-beta.9", "iOS", false, true, null),
            new("v1.0-beta.9", "Legacy", false, true, null),
            new("v0.3.0", "Stable", false, false, null),
            new("0.2.3-insider.8", "Missing v", false, true, null),
            new("v0.2.3-insider.6", "Insider 6", false, true, DateTimeOffset.Parse("2026-10-01T00:00:00Z")),
        ];

        ApplicationReleaseInfo? insider = GitHubApplicationReleaseSource.SelectLatest(releases, ApplicationReleaseChannel.Insider);
        insider!.Version.Tag.Should().Be("v0.2.3-insider.6");
        insider.Name.Should().Be("Insider 6");

        ApplicationReleaseInfo? stable = GitHubApplicationReleaseSource.SelectLatest(releases, ApplicationReleaseChannel.Stable);
        stable!.Version.Tag.Should().Be("v0.3.0");

        GitHubApplicationReleaseSource.SelectLatest([], ApplicationReleaseChannel.Stable).Should().BeNull();
    }

    [Fact]
    public async Task Source_ParsesListingFromBoundedRequest()
    {
        const string json = """
            [
              {"tag_name":"v0.2.3-insider.6","name":"Insider 6","draft":false,"prerelease":true,"published_at":"2026-10-01T00:00:00Z"},
              {"tag_name":"v0.2.3","name":"Stable","draft":false,"prerelease":false,"published_at":"2026-09-01T00:00:00Z"}
            ]
            """;
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        ApplicationReleaseInfo? latest = await CreateSource(handler)
            .GetLatestReleaseAsync(ApplicationReleaseChannel.Insider, CancellationToken.None);

        latest!.Version.Version.Should().Be("0.2.3-insider.6");
        latest.PublishedAt.Should().Be(DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        handler.LastRequestUri.Should().Be(new Uri("https://api.github.com/repos/OlyForge3D/PrintFarmer/releases?per_page=50"));
    }

    [Fact]
    public async Task Source_ReportsRateLimit()
    {
        StubHandler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            return response;
        });

        Func<Task> act = () => CreateSource(handler).GetLatestReleaseAsync(ApplicationReleaseChannel.Stable, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("*rate limit*");
    }

    [Fact]
    public async Task Source_RejectsOversizedListing()
    {
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[GitHubApplicationReleaseSource.MaximumResponseBytes + 1]),
        });

        Func<Task> act = () => CreateSource(handler).GetLatestReleaseAsync(ApplicationReleaseChannel.Stable, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Source_RejectsMalformedListing()
    {
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        });

        Func<Task> act = () => CreateSource(handler).GetLatestReleaseAsync(ApplicationReleaseChannel.Stable, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public void State_NonReleaseBuild_IsUnknownInstalledVersion()
    {
        ApplicationReleaseUpdateState state = CreateState("development", new ManualTimeProvider());
        state.RecordSuccess(Release("v0.2.3-insider.6"));

        ApplicationReleaseUpdateStatusDto status = state.GetStatus();

        status.Status.Should().Be(ApplicationReleaseUpdateStatus.UnknownInstalledVersion);
        status.UpdateAvailable.Should().BeFalse();
        status.LatestVersion.Should().BeNull();
        status.InstalledVersion.Should().BeNull();
        status.UpgradeDocsUrl.Should().Be(ApplicationReleaseUpdateState.DefaultUpgradeDocsUrl);
    }

    [Fact]
    public void State_ReportsNotCheckedThenUpdateAvailable()
    {
        ApplicationReleaseUpdateState state = CreateState("0.2.3-insider.5", new ManualTimeProvider());
        state.GetStatus().Status.Should().Be(ApplicationReleaseUpdateStatus.NotChecked);

        state.RecordSuccess(Release("v0.2.3-insider.6"));
        ApplicationReleaseUpdateStatusDto status = state.GetStatus();

        status.Status.Should().Be(ApplicationReleaseUpdateStatus.UpdateAvailable);
        status.UpdateAvailable.Should().BeTrue();
        status.InstalledVersion.Should().Be("0.2.3-insider.5");
        status.Channel.Should().Be(ApplicationReleaseChannel.Insider);
        status.LatestVersion.Should().Be("0.2.3-insider.6");
        status.LatestTag.Should().Be("v0.2.3-insider.6");
        status.ReleaseUrl.Should().Be("https://github.com/OlyForge3D/PrintFarmer/releases/tag/v0.2.3-insider.6");
        status.UpgradeDocsUrl.Should().Be(
            "https://github.com/OlyForge3D/PrintFarmer/blob/v0.2.3-insider.6/docs/DEPLOYMENT.md#upgrading-to-a-new-release");
        status.LastSuccessfulCheckAt.Should().NotBeNull();
        status.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("0.2.3-insider.6")]
    [InlineData("0.2.3-insider.7")]
    public void State_InstalledAtOrAboveLatest_IsUpToDate(string installed)
    {
        ApplicationReleaseUpdateState state = CreateState(installed, new ManualTimeProvider());
        state.RecordSuccess(Release("v0.2.3-insider.6"));

        ApplicationReleaseUpdateStatusDto status = state.GetStatus();

        status.Status.Should().Be(ApplicationReleaseUpdateStatus.UpToDate);
        status.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public void State_NoPublishedRelease_IsUpToDate()
    {
        ApplicationReleaseUpdateState state = CreateState("0.2.3", new ManualTimeProvider());
        state.RecordSuccess(null);

        ApplicationReleaseUpdateStatusDto status = state.GetStatus();

        status.Status.Should().Be(ApplicationReleaseUpdateStatus.UpToDate);
        status.LatestVersion.Should().BeNull();
    }

    [Fact]
    public void State_FailureRetainsLastKnownReleaseAndBecomesStale()
    {
        ManualTimeProvider time = new();
        ApplicationReleaseUpdateState state = CreateState("0.2.3-insider.5", time);
        state.Configure(enabled: true, intervalSeconds: 600);
        state.RecordSuccess(Release("v0.2.3-insider.6"));
        DateTimeOffset? lastSuccess = state.GetStatus().LastSuccessfulCheckAt;

        time.Advance(TimeSpan.FromSeconds(1201));
        state.RecordFailure(new string('x', 500));
        ApplicationReleaseUpdateStatusDto status = state.GetStatus();

        status.Status.Should().Be(ApplicationReleaseUpdateStatus.CheckFailed);
        status.UpdateAvailable.Should().BeTrue();
        status.LatestVersion.Should().Be("0.2.3-insider.6");
        status.LastSuccessfulCheckAt.Should().Be(lastSuccess);
        status.LastCheckedAt.Should().BeAfter(lastSuccess!.Value);
        status.IsStale.Should().BeTrue();
        status.Error.Should().HaveLength(300);
        status.CheckIntervalSeconds.Should().Be(600);

        state.RecordSuccess(Release("v0.2.3-insider.6"));
        ApplicationReleaseUpdateStatusDto recovered = state.GetStatus();
        recovered.Status.Should().Be(ApplicationReleaseUpdateStatus.UpdateAvailable);
        recovered.Error.Should().BeNull();
        recovered.IsStale.Should().BeFalse();
    }

    [Fact]
    public void State_Disabled_HidesLatestRelease()
    {
        ApplicationReleaseUpdateState state = CreateState("0.2.3-insider.5", new ManualTimeProvider());
        state.RecordSuccess(Release("v0.2.3-insider.6"));
        state.Configure(enabled: false, intervalSeconds: 600);

        ApplicationReleaseUpdateStatusDto status = state.GetStatus();

        status.Status.Should().Be(ApplicationReleaseUpdateStatus.Disabled);
        status.UpdateAvailable.Should().BeFalse();
        status.LatestVersion.Should().BeNull();
        status.ReleaseUrl.Should().BeNull();
    }

    [Fact]
    public async Task CheckService_Success_RecordsResultAndReportsMonitor()
    {
        ApplicationReleaseUpdateState state = CreateState("0.2.3-insider.5", new ManualTimeProvider());
        Mock<IApplicationReleaseSource> source = new();
        source.Setup(s => s.GetLatestReleaseAsync(ApplicationReleaseChannel.Insider, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Release("v0.2.3-insider.6"));
        Mock<IBackgroundServiceMonitor> monitor = new();

        bool result = await CreateService(state, source.Object, monitor.Object).RunCheckAsync(600, DefaultTimeout, CancellationToken.None);

        result.Should().BeTrue();
        state.GetStatus().Status.Should().Be(ApplicationReleaseUpdateStatus.UpdateAvailable);
        monitor.Verify(m => m.ReportSuccess("ApplicationReleaseUpdateCheckService", 600), Times.Once);
    }

    [Fact]
    public async Task CheckService_Timeout_RecordsExplicitFailure()
    {
        ApplicationReleaseUpdateState state = CreateState("0.2.3", new ManualTimeProvider());
        Mock<IApplicationReleaseSource> source = new();
        source.Setup(s => s.GetLatestReleaseAsync(ApplicationReleaseChannel.Stable, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timeout"));
        Mock<IBackgroundServiceMonitor> monitor = new();

        bool result = await CreateService(state, source.Object, monitor.Object).RunCheckAsync(600, DefaultTimeout, CancellationToken.None);

        result.Should().BeFalse();
        ApplicationReleaseUpdateStatusDto status = state.GetStatus();
        status.Status.Should().Be(ApplicationReleaseUpdateStatus.CheckFailed);
        status.Error.Should().Be("GitHub release check timed out.");
        monitor.Verify(m => m.ReportError("ApplicationReleaseUpdateCheckService", "GitHub release check timed out."), Times.Once);
    }

    [Fact]
    public async Task CheckService_SlowResponseBody_TimesOutAndRecordsFailure()
    {
        ApplicationReleaseUpdateState state = CreateState("0.2.3", new ManualTimeProvider());
        // Headers arrive immediately; the body never completes, so only a whole-operation
        // deadline (not HttpClient.Timeout with ResponseHeadersRead) can end the check.
        using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        });
        Mock<IBackgroundServiceMonitor> monitor = new();

        bool result = await CreateService(state, CreateSource(handler), monitor.Object)
            .RunCheckAsync(600, TimeSpan.FromMilliseconds(200), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        result.Should().BeFalse();
        ApplicationReleaseUpdateStatusDto status = state.GetStatus();
        status.Status.Should().Be(ApplicationReleaseUpdateStatus.CheckFailed);
        status.Error.Should().Be("GitHub release check timed out.");
        status.LastCheckedAt.Should().NotBeNull();
        monitor.Verify(m => m.ReportError("ApplicationReleaseUpdateCheckService", "GitHub release check timed out."), Times.Once);
    }

    [Fact]
    public async Task CheckService_HostCancellation_PropagatesWithoutRecordingFailure()
    {
        ApplicationReleaseUpdateState state = CreateState("0.2.3", new ManualTimeProvider());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        Mock<IApplicationReleaseSource> source = new();
        source.Setup(s => s.GetLatestReleaseAsync(It.IsAny<ApplicationReleaseChannel>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> act = async () =>
            await CreateService(state, source.Object, Mock.Of<IBackgroundServiceMonitor>()).RunCheckAsync(600, DefaultTimeout, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        state.GetStatus().Status.Should().Be(ApplicationReleaseUpdateStatus.NotChecked);
    }

    [Fact]
    public async Task CheckService_NonReleaseBuild_NeverContactsSource()
    {
        ApplicationReleaseUpdateState state = CreateState("development", new ManualTimeProvider());
        Mock<IApplicationReleaseSource> source = new(MockBehavior.Strict);

        bool result = await CreateService(state, source.Object, Mock.Of<IBackgroundServiceMonitor>())
            .RunCheckAsync(600, DefaultTimeout, CancellationToken.None);

        result.Should().BeFalse();
        source.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(300, 5, true)]
    [InlineData(86400, 120, true)]
    [InlineData(299, 15, false)]
    [InlineData(86401, 15, false)]
    [InlineData(21600, 4, false)]
    [InlineData(21600, 121, false)]
    public void OptionsValidator_EnforcesBounds(int interval, int timeout, bool valid)
    {
        ApplicationReleaseUpdateOptions options = new() { IntervalSeconds = interval, HttpTimeoutSeconds = timeout };

        new ApplicationReleaseUpdateOptionsValidator().Validate(null, options).Succeeded.Should().Be(valid);
    }

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private static ApplicationReleaseVersion Parse(string value)
    {
        ApplicationReleaseVersion.TryParse(value, out ApplicationReleaseVersion? version).Should().BeTrue();
        return version!;
    }

    private static ApplicationReleaseInfo Release(string tag) => new(Parse(tag), tag, null);

    private static ApplicationReleaseUpdateState CreateState(string installed, TimeProvider timeProvider)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApplicationReleaseUpdateState.InstalledVersionConfigurationKey] = installed,
            })
            .Build();
        return new ApplicationReleaseUpdateState(configuration, timeProvider);
    }

    private static GitHubApplicationReleaseSource CreateSource(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

    private static ApplicationReleaseUpdateCheckService CreateService(
        ApplicationReleaseUpdateState state,
        IApplicationReleaseSource source,
        IBackgroundServiceMonitor monitor)
    {
        Mock<IOptionsMonitor<ApplicationReleaseUpdateOptions>> options = new();
        options.Setup(o => o.CurrentValue).Returns(new ApplicationReleaseUpdateOptions());
        return new ApplicationReleaseUpdateCheckService(
            NullLogger<ApplicationReleaseUpdateCheckService>.Instance,
            options.Object,
            monitor,
            state,
            source);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>A readable stream whose reads never complete until cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }
}
