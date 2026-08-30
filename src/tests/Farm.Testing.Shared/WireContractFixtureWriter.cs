using System.Diagnostics;
using System.Text.Json;

namespace Farm.Testing.Shared;

/// <summary>
/// Provenance metadata recorded for a single wire-contract fixture, per issue #2238's
/// requirement that every fixture record its endpoint/event, producing test, schema version,
/// and refresh commit.
/// </summary>
public sealed record WireContractFixtureProvenance(
    string Path,
    string Endpoint,
    string ProducingTest,
    string SchemaVersion,
    string RefreshCommit);

/// <summary>
/// Writes (in regeneration mode) or verifies (the default, every normal test run) a single
/// wire-contract corpus fixture. This is the mechanism that turns the checked-in corpus into a
/// live regression guard: once a fixture exists, every subsequent test run re-serializes the
/// real payload and structurally compares it against the checked-in JSON via
/// <see cref="JsonContractAssertions.AssertStructurallyEqual"/> — a drifted serializer
/// (renamed property, changed enum representation, etc.) fails the assertion and turns the
/// owning CI leg red, which is exactly the negative control issue #2238 requires.
///
/// Set the <c>WIRE_CONTRACT_REGEN=1</c> environment variable to (re)write fixtures from the
/// current real serialization output instead of verifying against what's checked in — this is
/// how the corpus is authored/refreshed, never something CI does by default.
/// </summary>
public static class WireContractFixtureWriter
{
    private static readonly SemaphoreSlim ManifestLock = new(1, 1);
    private static readonly Lazy<string> _refreshCommit = new(ResolveGitHeadSha);
    private static readonly JsonSerializerOptions PrettyPrintOptions = new() { WriteIndented = true };

    private static bool RegenerationMode =>
        string.Equals(Environment.GetEnvironmentVariable("WIRE_CONTRACT_REGEN"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Writes or verifies a fixture under <see cref="WireContractCorpusPaths.ApiRoot"/> or
    /// <see cref="WireContractCorpusPaths.NativeSlicerRoot"/> (pass <paramref name="corpusRoot"/>
    /// accordingly — callers should use one of those two properties, never a bespoke path, so
    /// the PrintFarmer DTO corpus and the native Orca corpus stay physically separate).
    /// </summary>
    /// <param name="corpusRoot">Either <see cref="WireContractCorpusPaths.ApiRoot"/> or <see cref="WireContractCorpusPaths.NativeSlicerRoot"/>.</param>
    /// <param name="relativePath">Fixture path relative to <paramref name="corpusRoot"/>, e.g. <c>printers/status.minimal.json</c>.</param>
    /// <param name="endpoint">The endpoint or SignalR event this fixture documents, e.g. <c>GET /api/printers/{id}/status</c> or <c>event printerupdated</c>.</param>
    /// <param name="producingTest">Fully-qualified producing test name, e.g. <c>Farm.Web.Api.Tests.Contracts.PrinterStatusContractTests.GetStatus_Minimal</c>.</param>
    /// <param name="schemaVersion">Schema version string for this fixture family; bump when the shape intentionally changes.</param>
    /// <param name="actualJson">The real serialized JSON produced by production serialization for this call.</param>
    /// <param name="volatilePaths">
    /// See <see cref="JsonContractAssertions.CompareStructurally"/>. Pass the exact paths (e.g.
    /// <c>"$.checkedAt"</c>) of any leaf whose value is intentionally non-deterministic between
    /// runs (timestamps, elapsed-time strings, generated ids) so the corpus still guards shape,
    /// naming, and enum-token drift without becoming flaky on data that legitimately changes.
    /// Leave null/empty for fully deterministic payloads.
    /// </param>
    public static async Task CaptureOrVerifyAsync(
        string corpusRoot,
        string relativePath,
        string endpoint,
        string producingTest,
        string schemaVersion,
        string actualJson,
        IReadOnlySet<string>? volatilePaths = null)
    {
        string fullPath = System.IO.Path.Join(corpusRoot, relativePath);
        using JsonDocument actualDocument = JsonDocument.Parse(actualJson);

        if (RegenerationMode || !File.Exists(fullPath))
        {
            string? directory = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            string pretty = JsonSerializer.Serialize(actualDocument.RootElement, PrettyPrintOptions);
            await File.WriteAllTextAsync(fullPath, pretty + Environment.NewLine);
            await RecordProvenanceAsync(new WireContractFixtureProvenance(
                Path: ToManifestRelativePath(corpusRoot, fullPath),
                Endpoint: endpoint,
                ProducingTest: producingTest,
                SchemaVersion: schemaVersion,
                RefreshCommit: _refreshCommit.Value));
            return;
        }

        string checkedInJson = await File.ReadAllTextAsync(fullPath);
        using JsonDocument expectedDocument = JsonDocument.Parse(checkedInJson);
        JsonContractAssertions.AssertStructurallyEqual(expectedDocument.RootElement, actualDocument.RootElement, volatilePaths);
    }

    private static string ToManifestRelativePath(string corpusRoot, string fullPath)
    {
        string corpusContainer = WireContractCorpusPaths.CorpusRoot;
        string relativeToCorpusRoot = System.IO.Path.GetRelativePath(corpusContainer, fullPath).Replace('\\', '/');
        return relativeToCorpusRoot;
    }

    private static async Task RecordProvenanceAsync(WireContractFixtureProvenance provenance)
    {
        await ManifestLock.WaitAsync();
        try
        {
            string manifestPath = WireContractCorpusPaths.ManifestPath;
            var entries = new List<WireContractFixtureProvenance>();
            if (File.Exists(manifestPath))
            {
                string existing = await File.ReadAllTextAsync(manifestPath);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    entries = JsonSerializer.Deserialize<List<WireContractFixtureProvenance>>(existing) ?? [];
                }
            }

            entries.RemoveAll(e => string.Equals(e.Path, provenance.Path, StringComparison.Ordinal));
            entries.Add(provenance);
            entries = [.. entries.OrderBy(e => e.Path, StringComparer.Ordinal)];

            string serialized = JsonSerializer.Serialize(entries, PrettyPrintOptions);
            await File.WriteAllTextAsync(manifestPath, serialized + Environment.NewLine);
        }
        finally
        {
            _ = ManifestLock.Release();
        }
    }

    private static string ResolveGitHeadSha()
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = WireContractCorpusPaths.CorpusRoot,
            };
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return "unknown";
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(output) ? "unknown" : output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}
