using System.Text.RegularExpressions;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Architecture-test regression guard for issue #1966: <c>MaintenanceHub</c> auto-joined every
/// authenticated connection to a farm-wide group, and <c>MaintenanceResolutionNotifier</c>
/// broadcast maintenance-completion events via <c>Clients.All</c> — both bypassing the
/// <c>maintenance:admin</c> gate enforced on the equivalent REST endpoints. This test scans every
/// SignalR hub / hub-context consumer under <c>src/api</c> for <c>Clients.All</c> so this class of
/// authorization bypass cannot silently reintroduce.
///
/// Companion to <see cref="QueueEnqueuePermissionArchitectureTests"/> (permission-gate
/// completeness) and <see cref="AuthorizeRolesGateArchitectureTests"/> (role-name gate), which
/// guard the same "every broadcast/action must carry a real authorization scope" principle for
/// different bypass shapes.
/// </summary>
public sealed class NoClientsAllArchitectureTests
{
    /// <summary>
    /// Explicit, minimal allowlist for genuine pre-existing exceptions unrelated to issue #1966.
    /// Add an entry here only with a comment explaining why the broadcast is intentionally
    /// unscoped; do not add an entry just to make a new violation pass.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pre-existing, unrelated to issue #1966: broadcasts a farm-wide filament-fallback-group
        // configuration change. Out of scope for the MaintenanceHub authorization fix; left
        // unchanged per "do not fix unrelated pre-existing issues" policy.
        "Controllers/FilamentFallbackGroupsController.cs",
    };

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Join(dir.FullName, "farm-web.sln")) || Directory.Exists(Path.Join(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root (farm-web.sln or .git) not found from current directory.");
    }

    [Fact(DisplayName = "Presubmit: no SignalR hub or hub-context consumer in src/api calls Clients.All")]
    public void NoClientsAllBroadcastsInApi()
    {
        string repoRoot = FindRepoRoot();
        string apiRoot = Path.Join(repoRoot, "src", "api");
        Assert.True(Directory.Exists(apiRoot), $"Expected {apiRoot} to exist.");

        Regex clientsAllPattern = new(@"\bClients\s*\.\s*All\b", RegexOptions.Compiled);

        List<string> offenders = [];

        foreach (string file in Directory.GetFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(apiRoot, file).Replace('\\', '/');
            if (relativePath.Split('/') is [.., var segment] && (segment == "bin" || segment == "obj"))
            {
                continue;
            }
            if (relativePath.Contains("/bin/", StringComparison.Ordinal) || relativePath.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            string content = File.ReadAllText(file);
            if (!clientsAllPattern.IsMatch(content))
            {
                continue;
            }

            if (Allowlist.Contains(relativePath))
            {
                continue;
            }

            offenders.Add(relativePath);
        }

        Assert.True(
            offenders.Count == 0,
            "The following files under src/api broadcast via Clients.All, bypassing per-group " +
            "authorization (see issue #1966). Scope the broadcast to an explicit, authorized " +
            $"group (e.g. Clients.Group(...) / Clients.Groups(...)), or add a justified entry to " +
            $"{nameof(Allowlist)} if a genuine exception applies. Offenders: " +
            string.Join(", ", offenders));
    }
}
