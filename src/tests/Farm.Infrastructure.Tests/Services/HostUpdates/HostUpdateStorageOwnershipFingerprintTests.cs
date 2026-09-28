using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Issue #3155: the storage ownership signal is authorization-bound (flipping it is
/// configuration drift) without changing the fingerprint of any host-owned configuration
/// recorded before the signal existed.
/// </summary>
public sealed class HostUpdateStorageOwnershipFingerprintTests
{
    private static readonly DatabaseProviderConfiguration Database = new()
    {
        Provider = "postgres",
        ConnectionString = "Host=db.example;Port=5432;Database=farm",
    };

    [Fact]
    public void HostOwnedStorage_KeepsTheFingerprintRecordedBeforeTheSignalExisted()
    {
        HostUpdateExecutionOptions options = Options();

        HostUpdateBaselineHashes.Configuration(options, Database).Should().Be(LegacyFingerprint(options));
    }

    [Fact]
    public void ExternallyOwnedStorage_ChangesTheFingerprint()
    {
        HostUpdateExecutionOptions hostOwned = Options();
        HostUpdateExecutionOptions external = Options();
        external.StorageExternallyOwned = true;

        HostUpdateBaselineHashes.Configuration(external, Database)
            .Should().NotBe(HostUpdateBaselineHashes.Configuration(hostOwned, Database));
    }

    [Fact]
    public void StorageAndDatabaseOwnership_AreDistinctFingerprintInputs()
    {
        HostUpdateExecutionOptions storage = Options();
        storage.StorageExternallyOwned = true;
        HostUpdateExecutionOptions database = Options();
        database.DatabaseExternallyOwned = true;

        HostUpdateBaselineHashes.Configuration(storage, Database)
            .Should().NotBe(HostUpdateBaselineHashes.Configuration(database, Database));
    }

    private static HostUpdateExecutionOptions Options() => new()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "hu-fingerprint-root"),
        ComposeFiles = [Path.Combine(Path.GetTempPath(), "hu-fingerprint-" + Guid.Empty.ToString("N"), "absent.yml")],
    };

    // Mirrors the pre-#3155 fingerprint shape exactly; the compose file is absent, so its hash is "missing".
    private static string LegacyFingerprint(HostUpdateExecutionOptions options) =>
        "sha256:" + HostUpdateBaselineHashes.Hash(new
        {
            options.RootDirectory,
            options.ComposeProjectName,
            ComposeFiles = options.ComposeFiles.Select(file => new { File = file, Sha256 = "missing" }).ToArray(),
            ServiceMappings = options.ServiceMappings
                .OrderBy(m => m.ServiceId, StringComparer.Ordinal)
                .Select(m => new { m.ServiceId, m.ComposeServiceName, m.ImageEnvironmentVariable, m.ImageRepository })
                .ToArray(),
            OwnedDirectories = Ordered(options.OwnedDirectories),
            OptionalOwnedDirectories = options.OptionalOwnedDirectories.Order(StringComparer.Ordinal).ToArray(),
            HostExecutablePaths = Ordered(options.HostExecutablePaths),
            ActiveServiceIds = options.ActiveServiceIds.Order(StringComparer.Ordinal).ToArray(),
            Provider = "postgres",
            SqliteDataSource = (string?)null,
            DatabaseServer = HostUpdateBaselineHashes.DatabaseServerIdentity(Database),
            options.DatabaseExternallyOwned,
        });

    private static string[][] Ordered(IEnumerable<KeyValuePair<string, string>> values) =>
        [.. values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new[] { pair.Key, pair.Value })];
}
