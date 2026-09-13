using System.Reflection;
using System.Text.RegularExpressions;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>Reads application assembly metadata without treating engine versions or aliases as builds.</summary>
public static partial class ApplicationBuildObservation
{
    /// <summary>Parses legacy and canonical informational build metadata. Missing evidence stays null.</summary>
    public static (string? Version, string? Commit) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        string[] parts = value.Split('+', 2);
        string? version = !BuildVersionPattern().IsMatch(parts[0]) || parts[0] is "0.0.0" or "0.0.0.0" ? null : parts[0];
        string? commit = parts.Length == 2 ? parts[1] : null;
        if (commit?.StartsWith("sha.", StringComparison.Ordinal) == true)
        {
            commit = commit[4..];
        }

        return (version, commit is not null && FullCommit().IsMatch(commit) ? commit.ToLowerInvariant() : null);
    }

    /// <summary>Reads only the supplied component assembly, never another process's version.</summary>
    public static (string? Version, string? Commit) FromAssembly(Assembly assembly) =>
        Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildVersionPattern();

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullCommit();
}
