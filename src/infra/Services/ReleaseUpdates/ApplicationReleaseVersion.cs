using System.Globalization;
using System.Text.RegularExpressions;

namespace Farm.Infrastructure.Services.ReleaseUpdates;

/// <summary>Release channel a PrintFarmer application version belongs to.</summary>
public enum ApplicationReleaseChannel
{
    /// <summary>Stable releases tagged <c>vX.Y.Z</c>.</summary>
    Stable = 0,

    /// <summary>Insider pre-releases tagged <c>vX.Y.Z-insider.N</c>.</summary>
    Insider = 1,
}

/// <summary>
/// A parsed PrintFarmer release version (<c>X.Y.Z</c> or <c>X.Y.Z-insider.N</c>, with an
/// optional leading <c>v</c>). Ordering mirrors <c>scripts/ci/release-policy.mjs</c>:
/// numeric major/minor/patch, a stable release outranks every insider build of the same
/// base version, and insider builds order by their numeric <c>N</c>. Any other tag shape
/// (e.g. <c>ios/v1.0-beta.1</c>, <c>v1.0-beta.1</c>) is rejected.
/// </summary>
public sealed partial record ApplicationReleaseVersion : IComparable<ApplicationReleaseVersion>
{
    private ApplicationReleaseVersion(int major, int minor, int patch, int? insiderNumber)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        InsiderNumber = insiderNumber;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The insider build number, or <see langword="null"/> for a stable release.</summary>
    public int? InsiderNumber { get; }

    public ApplicationReleaseChannel Channel =>
        InsiderNumber is null ? ApplicationReleaseChannel.Stable : ApplicationReleaseChannel.Insider;

    /// <summary>Version without the leading <c>v</c> — the published container image tag.</summary>
    public string Version => InsiderNumber is null
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-insider.{InsiderNumber}");

    /// <summary>Git tag / GitHub release tag (<c>v</c>-prefixed).</summary>
    public string Tag => $"v{Version}";

    /// <summary>Parses a version or tag, returning <see langword="false"/> for anything that is
    /// not a well-formed stable or insider release identifier.</summary>
    public static bool TryParse(string? value, out ApplicationReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Match match = VersionPattern().Match(value.Trim());
        if (!match.Success
            || !TryParseComponent(match.Groups["major"].Value, out int major)
            || !TryParseComponent(match.Groups["minor"].Value, out int minor)
            || !TryParseComponent(match.Groups["patch"].Value, out int patch))
        {
            return false;
        }

        int? insider = null;
        if (match.Groups["insider"].Success)
        {
            if (!TryParseComponent(match.Groups["insider"].Value, out int insiderNumber))
            {
                return false;
            }

            insider = insiderNumber;
        }

        version = new ApplicationReleaseVersion(major, minor, patch, insider);
        return true;
    }

    public int CompareTo(ApplicationReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        return (InsiderNumber, other.InsiderNumber) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            ({ } left, { } right) => left.CompareTo(right),
        };
    }

    public override string ToString() => Version;

    public static bool operator <(ApplicationReleaseVersion? left, ApplicationReleaseVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(ApplicationReleaseVersion? left, ApplicationReleaseVersion? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(ApplicationReleaseVersion? left, ApplicationReleaseVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(ApplicationReleaseVersion? left, ApplicationReleaseVersion? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    private static bool TryParseComponent(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    [GeneratedRegex(
        @"^v?(?<major>0|[1-9]\d{0,8})\.(?<minor>0|[1-9]\d{0,8})\.(?<patch>0|[1-9]\d{0,8})(?:-insider\.(?<insider>[1-9]\d{0,8}))?$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();
}
