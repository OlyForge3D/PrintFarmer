using Farm.Infrastructure.Services.ReleaseUpdates;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Outcome of the farm-admin application release update check. Serialized as a string via
/// <c>JsonStringEnumConverter</c>.
/// </summary>
public enum ApplicationReleaseUpdateStatus
{
    /// <summary>The installed version is the newest published release on its channel.</summary>
    UpToDate = 0,

    /// <summary>A strictly newer release is published on the installed version's channel.</summary>
    UpdateAvailable = 1,

    /// <summary>The background check has not completed its first attempt yet.</summary>
    NotChecked = 2,

    /// <summary>The most recent check failed; any previously discovered release is retained.</summary>
    CheckFailed = 3,

    /// <summary>The check is disabled by configuration.</summary>
    Disabled = 4,

    /// <summary>This build has no release version (e.g. a source/development build), so no
    /// comparison is possible and GitHub is never contacted.</summary>
    UnknownInstalledVersion = 5,
}

/// <summary>
/// Cached result of the periodic GitHub release check, returned by
/// <c>GET /api/admin/release-updates</c>. Reading it never contacts GitHub.
/// </summary>
public sealed record ApplicationReleaseUpdateStatusDto
{
    public required ApplicationReleaseUpdateStatus Status { get; init; }

    /// <summary>True when the last known release on the installed channel is strictly newer
    /// than the installed version, even if the latest check attempt failed.</summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary>Installed release version without the leading <c>v</c>, or null when unknown.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Release channel derived from the installed version.</summary>
    public ApplicationReleaseChannel? Channel { get; init; }

    /// <summary>Newest known release version (also its container image tag), without <c>v</c>.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>Newest known release Git tag (<c>v</c>-prefixed).</summary>
    public string? LatestTag { get; init; }

    public string? LatestReleaseName { get; init; }

    public DateTimeOffset? LatestPublishedAt { get; init; }

    /// <summary>Server-constructed GitHub release page for <see cref="LatestTag"/>.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>Time of the most recent check attempt (success or failure).</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }

    public DateTimeOffset? LastSuccessfulCheckAt { get; init; }

    /// <summary>True when the last successful check is older than two check intervals.</summary>
    public required bool IsStale { get; init; }

    /// <summary>Error from the most recent failed check, or null after a success.</summary>
    public string? Error { get; init; }

    public required int CheckIntervalSeconds { get; init; }

    /// <summary>Operator documentation describing the safe upgrade procedure, pinned to the
    /// latest known release tag when one is known.</summary>
    public required string UpgradeDocsUrl { get; init; }
}
