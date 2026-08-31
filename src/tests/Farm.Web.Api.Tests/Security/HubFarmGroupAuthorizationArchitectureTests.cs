using System.Text.RegularExpressions;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Architecture-test regression guard for issue #2300 — the third occurrence of this exact bug
/// class (after #1324 and #1966): a SignalR hub's <c>OnConnectedAsync</c> auto-joins every
/// authenticated connection to the farm-wide <c>AuthorizedHubGroups.Farm</c> group with no
/// permission check, leaking farm-wide broadcast data (printer status, maintenance alerts,
/// harvest progress, etc.) to any authenticated user regardless of their actual permissions —
/// bypassing whatever REST-side gate protects the equivalent data.
///
/// This test scans every hub file under <c>src/api</c>, <c>src/infra</c>, <c>src/modules</c>, and
/// <c>src/slicer</c> for an <c>OnConnectedAsync</c> method body that references
/// <c>AuthorizedHubGroups.Farm</c> and asserts the reference is always nested inside a
/// permission-gate <c>if</c> (i.e. a condition mentioning <c>HasPermission(</c> or
/// <c>IsFarmAdmin(</c>) — mirroring the fix shipped for <c>MaintenanceHub</c> (#1966) and
/// <c>HarvestHub</c> (#2300) so a fourth occurrence of this exact bug class cannot land silently,
/// no matter which hub or assembly it appears in.
///
/// Companion to <see cref="NoClientsAllArchitectureTests"/> (unscoped <c>Clients.All</c>
/// broadcasts) and <see cref="QueueEnqueuePermissionArchitectureTests"/> (permission-gate
/// completeness on job enqueue), which guard the same "every broadcast/join must carry a real
/// authorization scope" principle for different bypass shapes.
/// </summary>
public sealed class HubFarmGroupAuthorizationArchitectureTests
{
    /// <summary>
    /// Explicit, minimal allowlist for genuine pre-existing exceptions. Add an entry here only
    /// with a comment explaining why the join is intentionally unguarded; do not add an entry
    /// just to make a new violation pass.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex OnConnectedAsyncPattern = new(
        @"public\s+override\s+(?:async\s+)?Task\s+OnConnectedAsync\s*\(\s*\)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex GuardConditionPattern = new(
        @"\b(?:HasPermission|IsFarmAdmin)\s*\(",
        RegexOptions.Compiled);

    private static string FindRepoRoot()
    {
        // Look for .git specifically (a directory in a normal clone, or a "gitdir: ..." pointer
        // file in a worktree checkout) rather than farm-web.sln: farm-web.sln lives at
        // <repo-root>/src/farm-web.sln, so checking for it directly would stop one level too
        // early and misidentify src/ itself as the repo root.
        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            string gitPath = Path.Join(dir.FullName, ".git");
            if (File.Exists(gitPath) || Directory.Exists(gitPath))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root (.git) not found from current directory.");
    }

    /// <summary>
    /// Extracts every brace-matched <c>OnConnectedAsync</c> method body found in
    /// <paramref name="content"/>, using <see cref="OnConnectedAsyncPattern"/> to find each
    /// method's opening brace and then walking forward counting nested braces (ignoring string
    /// and char literals) until the matching closing brace is found.
    /// </summary>
    private static IEnumerable<string> ExtractOnConnectedAsyncBodies(string content)
    {
        foreach (Match match in OnConnectedAsyncPattern.Matches(content))
        {
            int openBraceIndex = match.Index + match.Length - 1;
            int depth = 0;
            int i = openBraceIndex;
            bool inString = false;
            bool inChar = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            while (i < content.Length)
            {
                char c = content[i];
                char next = i + 1 < content.Length ? content[i + 1] : '\0';
                int advance = 1;
                bool closed = false;

                if (inLineComment)
                {
                    if (c == '\n')
                    {
                        inLineComment = false;
                    }
                }
                else if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        advance = 2;
                    }
                }
                else if (inString)
                {
                    if (c == '\\')
                    {
                        advance = 2;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else if (inChar)
                {
                    if (c == '\\')
                    {
                        advance = 2;
                    }
                    else if (c == '\'')
                    {
                        inChar = false;
                    }
                }
                else if (c == '/' && next == '/')
                {
                    inLineComment = true;
                    advance = 2;
                }
                else if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    advance = 2;
                }
                else if (c == '"')
                {
                    inString = true;
                }
                else if (c == '\'')
                {
                    inChar = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return content[openBraceIndex..(i + 1)];
                        closed = true;
                    }
                }

                if (closed)
                {
                    break;
                }

                i += advance;
            }
        }
    }

    /// <summary>
    /// Determines whether every occurrence of <c>AuthorizedHubGroups.Farm</c> in
    /// <paramref name="methodBody"/> is nested inside an <c>if</c> block whose condition text
    /// mentions <c>HasPermission(</c> or <c>IsFarmAdmin(</c>. Walks the body char-by-char tracking
    /// a brace-depth guard stack: the frame pushed for each <c>{</c> records whether the
    /// immediately preceding (non-brace) text back to the previous <c>{</c>/<c>}</c>/<c>;</c>
    /// contains a guard-condition call. A <c>AuthorizedHubGroups.Farm</c> join is guarded if any
    /// frame currently on the stack is a guard frame.
    /// </summary>
    private static bool AllFarmGroupJoinsAreGuarded(string methodBody, out bool referencesFarmGroup)
    {
        referencesFarmGroup = false;
        var guardStack = new Stack<bool>();
        int lastStatementBoundary = 0;

        for (int i = 0; i < methodBody.Length; i++)
        {
            char c = methodBody[i];

            if (c == '{')
            {
                string precedingText = methodBody[lastStatementBoundary..i];
                bool isGuardFrame = GuardConditionPattern.IsMatch(precedingText);
                guardStack.Push(isGuardFrame);
                lastStatementBoundary = i + 1;
                continue;
            }

            if (c == '}')
            {
                if (guardStack.Count > 0)
                {
                    guardStack.Pop();
                }
                lastStatementBoundary = i + 1;
                continue;
            }

            if (c == ';')
            {
                lastStatementBoundary = i + 1;
                continue;
            }

            if (c == 'A' && methodBody.AsSpan(i).StartsWith("AuthorizedHubGroups.Farm", StringComparison.Ordinal))
            {
                referencesFarmGroup = true;
                bool guarded = guardStack.Count > 0 && guardStack.Contains(true);
                if (!guarded)
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Fact(DisplayName = "Presubmit: every hub's OnConnectedAsync only joins AuthorizedHubGroups.Farm behind a permission check")]
    public void EveryFarmGroupAutoJoinIsPermissionGated()
    {
        string repoRoot = FindRepoRoot();

        string[] scanRoots =
        [
            Path.Join(repoRoot, "src", "api"),
            Path.Join(repoRoot, "src", "infra"),
            Path.Join(repoRoot, "src", "modules"),
            Path.Join(repoRoot, "src", "slicer"),
        ];

        List<string> offenders = [];
        bool foundAnyFarmGroupJoin = false;

        foreach (string scanRoot in scanRoots)
        {
            Assert.True(Directory.Exists(scanRoot), $"Expected {scanRoot} to exist.");

            foreach (string file in Directory.GetFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(scanRoot, file).Replace('\\', '/');
                if (relativePath.Contains("/bin/", StringComparison.Ordinal) || relativePath.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                string content = File.ReadAllText(file);
                if (!content.Contains("OnConnectedAsync", StringComparison.Ordinal) ||
                    !content.Contains("AuthorizedHubGroups.Farm", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string body in ExtractOnConnectedAsyncBodies(content))
                {
                    bool allGuarded = AllFarmGroupJoinsAreGuarded(body, out bool referencesFarmGroup);
                    if (!referencesFarmGroup)
                    {
                        continue;
                    }

                    foundAnyFarmGroupJoin = true;
                    if (allGuarded || Allowlist.Contains(relativePath))
                    {
                        continue;
                    }

                    offenders.Add(relativePath);
                }
            }
        }

        // Sanity check: this test is only meaningful if it actually found the guarded joins in
        // HarvestHub/MaintenanceHub that motivate it — an empty scan (e.g. a moved/renamed hub
        // directory breaking the OnConnectedAsync regex) would let this test pass vacuously
        // while providing zero regression coverage.
        Assert.True(
            foundAnyFarmGroupJoin,
            "Expected to find at least one OnConnectedAsync method referencing " +
            "AuthorizedHubGroups.Farm (e.g. HarvestHub, MaintenanceHub) — if none was found, " +
            "this test's scan roots or OnConnectedAsync detection regex may need updating.");

        Assert.True(
            offenders.Count == 0,
            "The following hub files join AuthorizedHubGroups.Farm in OnConnectedAsync with no " +
            "permission check, auto-subscribing every authenticated connection to the farm-wide " +
            "broadcast group regardless of permissions (see issues #1324, #1966, #2300). Gate the " +
            "join behind a check such as PrintFarmerPermissions.HasPermission(Context.User!, " +
            $"\"<resource>:admin\"), or add a justified entry to {nameof(Allowlist)} if a genuine " +
            "exception applies. Offenders: " + string.Join(", ", offenders));
    }
}
