using System.Runtime.CompilerServices;

namespace Farm.Slicer.Module.Tests.TestInfrastructure;

/// <summary>
/// Central helper for constructing test-local temporary paths that live inside the repository
/// instead of the system temp directory. This avoids macOS privacy / TCC prompts that can occur
/// when accessing certain per-user temporary folders (e.g. /private/var/folders/...).
/// All tests should use this helper when creating ephemeral SQLite databases or file storage.
/// </summary>
public static class TestPaths
{
    private static readonly Lazy<string> _root = new(() =>
    {
        // Locate the repo root by walking up from the test assembly location until we find a marker
        // (farm-web.sln). As a fallback we use the current working directory.
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "farm-web.sln")))
            {
                return current;
            }
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }
        return Directory.GetCurrentDirectory();
    });

    /// <summary>
    /// Root directory for test-specific temp artifacts inside the repository.
    /// </summary>
    public static string RepoTempRoot => EnsureCreated(Path.Combine(_root.Value, "src", "tests", "_temp"));

    /// <summary>
    /// Returns a unique temp directory for a specific test context (e.g. class or scenario).
    /// </summary>
    public static string GetUniqueTempDirectory([CallerMemberName] string? name = null)
    {
        string dir = Path.Combine(RepoTempRoot, name ?? "unnamed", Guid.NewGuid().ToString("N"));
        return EnsureCreated(dir);
    }

    /// <summary>
    /// Returns a file path under RepoTempRoot (parent folders are created if needed).
    /// </summary>
    public static string GetTempFilePath(string fileName, [CallerMemberName] string? name = null)
    {
        string dir = EnsureCreated(Path.Combine(RepoTempRoot, name ?? "files"));
        return Path.Combine(dir, fileName);
    }

    private static string EnsureCreated(string path)
    {
        if (!Directory.Exists(path))
        {
            _ = Directory.CreateDirectory(path);
        }
        return path;
    }
}
