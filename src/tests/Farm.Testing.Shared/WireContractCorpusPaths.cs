namespace Farm.Testing.Shared;

/// <summary>
/// Resolves the single canonical wire-contract fixture corpus directory
/// (<c>fixtures/wire-contracts/</c> at the repository root — a sibling of <c>src/</c> and
/// <c>mobile/</c>) so it is reachable by relative path from .NET tests, the Vitest suite
/// under <c>src/Web/ReactApp/</c>, and the Xcode project under <c>mobile/</c> without nesting
/// one consumer's tree inside another's. See issue #2238.
/// </summary>
public static class WireContractCorpusPaths
{
    private static readonly Lazy<string> _repoRoot = new(() =>
    {
        // Walk up from the test assembly location looking for the ".NET solution root"
        // marker (farm-web.sln lives directly under <repo-root>/src), mirroring
        // TestPaths's own marker walk, then step up one more level to the repo root.
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Join(current, "farm-web.sln")))
            {
                DirectoryInfo? repoRoot = Directory.GetParent(current);
                return repoRoot?.FullName ?? current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        // Never fall back to the current working directory: a misresolved root combined
        // with the fixture writer's "missing fixture" handling must fail loudly, not
        // silently redirect every fixture in the corpus to a fresh, empty directory.
        throw new InvalidOperationException(
            $"Could not resolve the repository root from '{AppContext.BaseDirectory}': " +
            "no ancestor directory contains farm-web.sln.");
    });

    /// <summary>Root of the canonical corpus: <c>&lt;repo-root&gt;/fixtures/wire-contracts</c>.</summary>
    public static string CorpusRoot => Path.Join(_repoRoot.Value, "fixtures", "wire-contracts");

    /// <summary>PrintFarmer DTO fixtures (camelCase, string enums) produced by real ASP.NET/SignalR serialization.</summary>
    public static string ApiRoot => Path.Join(CorpusRoot, "api");

    /// <summary>
    /// Native OrcaSlicer snake_case fixtures (e.g. <c>compatible_printers</c>). Never merged
    /// with the PrintFarmer DTO corpus under <see cref="ApiRoot"/>.
    /// </summary>
    public static string NativeSlicerRoot => Path.Join(CorpusRoot, "native-slicer");

    /// <summary>Provenance registry: one entry per fixture (endpoint/event, producing test, schema version, refresh commit).</summary>
    public static string ManifestPath => Path.Join(CorpusRoot, "manifest.json");
}
