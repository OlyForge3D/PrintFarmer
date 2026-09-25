using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// What the host looked like when an update was authorized (issues #3047, #3050): the prior
/// installed state, the configuration the recovery engine acts on, the pinned release trust root
/// and -- from schema 2 -- the database-side manifest binding for the release
/// (<see cref="ReadOnlyHostUpdateManifestBindingReader.NoBinding"/> when none was bound).
/// Journaled on the <c>accepted</c> activity, outside the request binding, so recovery can
/// compare the host against the authorized baseline instead of heuristics.
/// </summary>
public sealed record HostUpdateAuthorizationBaseline(
    int SchemaVersion,
    string InstalledStateHash,
    string ConfigurationFingerprint,
    string TrustRootFingerprint,
    string? ManifestBinding = null)
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>The oldest schema recovery still understands; schema 1 predates <see cref="ManifestBinding"/>.</summary>
    public const int MinimumSupportedSchemaVersion = 1;
}

/// <summary>Captures the <see cref="HostUpdateAuthorizationBaseline"/> at authorization time.</summary>
public interface IHostUpdateAuthorizationBaselineProvider
{
    Task<HostUpdateAuthorizationBaseline> CaptureAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>Reads the baseline from the same stores and configuration the recovery CLI observes.</summary>
public sealed class HostUpdateAuthorizationBaselineProvider(
    IInstalledHostStateStore installedStateStore,
    HostUpdateExecutionOptions options,
    DatabaseProviderConfiguration database,
    IHostUpdateManifestBindingReader manifestBindingReader) : IHostUpdateAuthorizationBaselineProvider
{
    public async Task<HostUpdateAuthorizationBaseline> CaptureAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstalledHostState? installed = await installedStateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        string manifestBinding = await manifestBindingReader.ReadAsync(request.ReleaseId, cancellationToken).ConfigureAwait(false);
        return new(
            HostUpdateAuthorizationBaseline.CurrentSchemaVersion,
            HostUpdateBaselineHashes.InstalledState(installed),
            HostUpdateBaselineHashes.Configuration(options, database),
            HostUpdateTrustRoot.Fingerprint,
            manifestBinding);
    }
}

/// <summary>
/// Canonical hashes shared by the executor (at authorization) and the recovery CLI (at recovery),
/// so both sides of a drift comparison are computed identically.
/// </summary>
public static class HostUpdateBaselineHashes
{
    public const string NoInstalledState = "none";

    /// <summary>Content hash of the installed state (<see cref="NoInstalledState"/> when absent), independent of dictionary order.</summary>
    public static string InstalledState(InstalledHostState? installed) => installed is null
        ? NoInstalledState
        : "sha256:" + Hash(new
        {
            installed.ReleaseId,
            installed.ManifestDigest,
            ServiceDigests = Ordered(installed.ServiceDigests),
            installed.Topology,
            RecordedAt = installed.RecordedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ServicePlatforms = installed.ServicePlatforms is null ? null : Ordered(installed.ServicePlatforms),
        });

    /// <summary>
    /// Fingerprint of the configuration the recovery engine acts on. Credentials are never
    /// included: only the provider and, for SQLite, the data-source path contribute.
    /// </summary>
    public static string Configuration(HostUpdateExecutionOptions options, DatabaseProviderConfiguration database)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(database);
        return "sha256:" + Hash(new
        {
            options.RootDirectory,
            options.ComposeProjectName,
            ComposeFiles = options.ComposeFiles.Select(file => new { File = file, Sha256 = FileHash(file) }).ToArray(),
            ServiceMappings = options.ServiceMappings
                .OrderBy(m => m.ServiceId, StringComparer.Ordinal)
                .Select(m => new { m.ServiceId, m.ComposeServiceName, m.ImageEnvironmentVariable, m.ImageRepository })
                .ToArray(),
            OwnedDirectories = Ordered(options.OwnedDirectories),
            OptionalOwnedDirectories = options.OptionalOwnedDirectories.Order(StringComparer.Ordinal).ToArray(),
            HostExecutablePaths = Ordered(options.HostExecutablePaths),
            ActiveServiceIds = options.ActiveServiceIds.Order(StringComparer.Ordinal).ToArray(),
            Provider = database.Provider.ToLowerInvariant(),
            SqliteDataSource = database.IsSqlite ? SqliteDataSource(database.ConnectionString) : null,
        });
    }

    internal static string Hash(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private static string[][] Ordered(IEnumerable<KeyValuePair<string, string>> values) =>
        [.. values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new[] { pair.Key, pair.Value })];

    private static string FileHash(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return File.Exists(full) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))) : "missing";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return "unreadable";
        }
    }

    private static string? SqliteDataSource(string connectionString)
    {
        try
        {
            return new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
