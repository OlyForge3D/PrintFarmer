using Farm.Infrastructure.Dtos;
using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Services.ReleaseUpdates;

/// <summary>Read side of the cached release update check.</summary>
public interface IApplicationReleaseUpdateStatusProvider
{
    ApplicationReleaseUpdateStatusDto GetStatus();
}

/// <summary>
/// Process-wide, thread-safe cache of the latest release check (issue #3281). Written only by
/// <see cref="ApplicationReleaseUpdateCheckService"/>; read by the admin endpoint so client
/// polling never reaches GitHub. Nothing is persisted.
/// </summary>
public sealed class ApplicationReleaseUpdateState : IApplicationReleaseUpdateStatusProvider
{
    /// <summary>Configuration key populated from <c>PFARM__SourceInfo__Version</c> by release images.</summary>
    public const string InstalledVersionConfigurationKey = "SourceInfo:Version";

    public const string ReleasePageBaseUrl = "https://github.com/OlyForge3D/PrintFarmer/releases/tag/";

    private const string RepositoryBlobBaseUrl = "https://github.com/OlyForge3D/PrintFarmer/blob/";

    private const string UpgradeDocsPath = "/docs/DEPLOYMENT.md#upgrading-to-a-new-release";

    /// <summary>Fallback upgrade documentation when no release has been discovered.</summary>
    public const string DefaultUpgradeDocsUrl = RepositoryBlobBaseUrl + "main" + UpgradeDocsPath;

    private const int MaximumErrorLength = 300;

    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private bool _enabled = true;
    private int _intervalSeconds = new ApplicationReleaseUpdateOptions().IntervalSeconds;
    private ApplicationReleaseInfo? _latest;
    private DateTimeOffset? _lastCheckedAt;
    private DateTimeOffset? _lastSuccessfulCheckAt;
    private string? _error;

    public ApplicationReleaseUpdateState(IConfiguration configuration, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        InstalledVersion = ApplicationReleaseVersion.TryParse(
            configuration[InstalledVersionConfigurationKey],
            out ApplicationReleaseVersion? installed)
            ? installed
            : null;
    }

    /// <summary>The installed release identity, or null for non-release builds.</summary>
    public ApplicationReleaseVersion? InstalledVersion { get; }

    public void Configure(bool enabled, int intervalSeconds)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _intervalSeconds = intervalSeconds;
        }
    }

    public void RecordSuccess(ApplicationReleaseInfo? latest)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            _latest = latest;
            _lastCheckedAt = now;
            _lastSuccessfulCheckAt = now;
            _error = null;
        }
    }

    /// <summary>Records a failed attempt while retaining the last successful result.</summary>
    public void RecordFailure(string error)
    {
        string message = string.IsNullOrWhiteSpace(error) ? "Release check failed." : error.Trim();
        if (message.Length > MaximumErrorLength)
        {
            message = message[..MaximumErrorLength];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            _lastCheckedAt = now;
            _error = message;
        }
    }

    public ApplicationReleaseUpdateStatusDto GetStatus()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            ApplicationReleaseVersion? installed = InstalledVersion;
            ApplicationReleaseInfo? latest = installed is null ? null : _latest;
            bool updateAvailable = installed is not null
                && _enabled
                && latest is not null
                && latest.Version.Channel == installed.Channel
                && latest.Version > installed;
            bool isStale = _enabled
                && installed is not null
                && _lastSuccessfulCheckAt is { } lastSuccess
                && now - lastSuccess > TimeSpan.FromSeconds(_intervalSeconds * 2L);

            ApplicationReleaseUpdateStatus status = installed is null
                ? ApplicationReleaseUpdateStatus.UnknownInstalledVersion
                : !_enabled
                    ? ApplicationReleaseUpdateStatus.Disabled
                    : _error is not null
                        ? ApplicationReleaseUpdateStatus.CheckFailed
                        : _lastSuccessfulCheckAt is null
                            ? ApplicationReleaseUpdateStatus.NotChecked
                            : updateAvailable
                                ? ApplicationReleaseUpdateStatus.UpdateAvailable
                                : ApplicationReleaseUpdateStatus.UpToDate;

            bool showLatest = installed is not null && _enabled && latest is not null;
            return new ApplicationReleaseUpdateStatusDto
            {
                Status = status,
                UpdateAvailable = updateAvailable,
                InstalledVersion = installed?.Version,
                Channel = installed?.Channel,
                LatestVersion = showLatest ? latest!.Version.Version : null,
                LatestTag = showLatest ? latest!.Version.Tag : null,
                LatestReleaseName = showLatest ? latest!.Name : null,
                LatestPublishedAt = showLatest ? latest!.PublishedAt : null,
                ReleaseUrl = showLatest ? ReleasePageBaseUrl + Uri.EscapeDataString(latest!.Version.Tag) : null,
                LastCheckedAt = _lastCheckedAt,
                LastSuccessfulCheckAt = _lastSuccessfulCheckAt,
                IsStale = isStale,
                Error = _enabled && installed is not null ? _error : null,
                CheckIntervalSeconds = _intervalSeconds,
                UpgradeDocsUrl = showLatest
                    ? RepositoryBlobBaseUrl + Uri.EscapeDataString(latest!.Version.Tag) + UpgradeDocsPath
                    : DefaultUpgradeDocsUrl,
            };
        }
    }
}
