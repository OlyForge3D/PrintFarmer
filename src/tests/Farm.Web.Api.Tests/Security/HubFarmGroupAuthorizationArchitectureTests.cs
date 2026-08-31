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
    /// Determines whether <paramref name="conditionText"/> guarantees a permission check on every
    /// path that can make it true. A bare <see cref="GuardConditionPattern"/> match on the whole
    /// condition text is insufficient in three ways this method closes:
    /// <list type="bullet">
    /// <item>Negation: <c>if (!HasPermission(...)) { join }</c> textually contains the guard call
    /// but actually re-creates the unconditional-join vulnerability (the join only happens when
    /// the caller LACKS the permission), so a negated call must not count as a guard.</item>
    /// <item>Disjunction bypass: <c>if (HasPermission(...) || true) { join }</c> contains a
    /// non-negated guard call, but the <c>|| true</c> alternative makes the whole condition true
    /// regardless of permission. Every top-level (paren-depth-0) <c>||</c>-separated disjunct must
    /// independently carry a non-negated guard call, or the disjunction is rejected outright — two
    /// real alternative permission checks (<c>HasPermission(a) || HasPermission(b)</c>) still pass
    /// since both disjuncts qualify.</item>
    /// <item>De Morgan negation of a compound expression: <c>if (!(a &amp;&amp;
    /// HasPermission(...))) { join }</c> has no <c>!</c> immediately before the call, but the
    /// enclosing <c>!( ... )</c> negates the whole conjunction, so the join actually fires when
    /// either <c>a</c> is false or the caller lacks the permission — the same vulnerability shape
    /// as a bare negation, just one parenthesis level removed.</item>
    /// </list>
    /// </summary>
    private static bool ConditionContainsPositiveGuard(string conditionText)
    {
        foreach (string disjunct in SplitTopLevelOr(conditionText))
        {
            if (!DisjunctHasPositiveGuard(disjunct))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits <paramref name="text"/> on <c>||</c> occurrences at paren-depth 0 (i.e. logical-OR
    /// alternatives of the overall condition, not <c>||</c> nested inside a sub-call's arguments).
    /// A condition with no top-level <c>||</c> yields a single element equal to the whole text.
    /// </summary>
    private static IEnumerable<string> SplitTopLevelOr(string text)
    {
        int depth = 0;
        int start = 0;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            int advance = 1;

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (depth == 0 && c == '|' && i + 1 < text.Length && text[i + 1] == '|')
            {
                yield return text[start..i];
                advance = 2;
                start = i + advance;
            }

            i += advance;
        }

        yield return text[start..];
    }

    /// <summary>
    /// Determines whether a single (already top-level-OR-split) condition fragment contains at
    /// least one non-negated call to <c>HasPermission(</c>/<c>IsFarmAdmin(</c>, per the negation
    /// rules documented on <see cref="ConditionContainsPositiveGuard"/>. A guard call counts as
    /// negated (and is skipped in favor of another match, if any) when it is either immediately
    /// preceded by <c>!</c> (<see cref="IsImmediatelyNegated"/>) or sits inside a parenthesized
    /// group that is itself preceded by <c>!</c> (<see cref="IsInsideNegatedGroup"/>) — the De
    /// Morgan case, e.g. <c>if (!(a &amp;&amp; HasPermission(...))) { join }</c>, where the call
    /// reads as positive in isolation but the enclosing negation flips it, so the join actually
    /// fires when <c>a</c> is false OR the caller LACKS the permission.
    /// </summary>
    private static bool DisjunctHasPositiveGuard(string disjunctText)
    {
        foreach (Match match in GuardConditionPattern.Matches(disjunctText))
        {
            if (IsImmediatelyNegated(disjunctText, match.Index) || IsInsideNegatedGroup(disjunctText, match.Index))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the guard call starting at <paramref name="matchIndex"/> is directly
    /// negated, i.e. preceded (after skipping its own qualified-receiver text and any wrapping
    /// whitespace/parens) by a <c>!</c>, such as <c>!HasPermission(</c> or
    /// <c>!(PrintFarmerPermissions.HasPermission(</c>.
    /// </summary>
    private static bool IsImmediatelyNegated(string text, int matchIndex)
    {
        int idx = matchIndex - 1;

        // Walk back over the (possibly qualified) receiver preceding the guard call name itself,
        // e.g. "PrintFarmerPermissions." in "PrintFarmerPermissions.HasPermission(", so a "!"
        // written before the qualifier (the common case) is not missed.
        while (idx >= 0 && (char.IsLetterOrDigit(text[idx]) || text[idx] is '.' or '_'))
        {
            idx--;
        }

        while (idx >= 0 && (char.IsWhiteSpace(text[idx]) || text[idx] == '('))
        {
            idx--;
        }

        return idx >= 0 && text[idx] == '!';
    }

    /// <summary>
    /// Checks whether the position <paramref name="matchIndex"/> sits inside one or more
    /// parenthesized groups — of arbitrary content, not just a bare wrapped call — where the
    /// nearest such enclosing <c>(</c> is itself immediately preceded by <c>!</c>. This catches
    /// a guard call buried inside a negated compound expression (<c>!(a &amp;&amp;
    /// HasPermission(...))</c>, <c>!(a || HasPermission(...))</c>) that <see
    /// cref="IsImmediatelyNegated"/> cannot see, because by De Morgan's laws negating a
    /// conjunction or disjunction negates every operand, including a permission check buried
    /// arbitrarily deep inside it.
    /// </summary>
    private static bool IsInsideNegatedGroup(string text, int matchIndex)
    {
        int depth = 0;

        for (int idx = matchIndex - 1; idx >= 0; idx--)
        {
            char c = text[idx];
            if (c == ')')
            {
                // A fully-closed nested group that occurs entirely before matchIndex — not an
                // enclosing scope of the match, just something the scan must skip past.
                depth++;
            }
            else if (c == '(')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                // depth == 0: this "(" has no matching ")" yet seen while scanning backward, so
                // it genuinely encloses matchIndex. Check whether it is itself negated, and if
                // not, keep scanning left for a further (outer) enclosing group.
                int before = idx - 1;
                while (before >= 0 && char.IsWhiteSpace(text[before]))
                {
                    before--;
                }

                if (before >= 0 && text[before] == '!')
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Strips a keyword-and-parenthesis wrapper (e.g. <c>if (</c> ... <c>)</c>, <c>else if (</c>
    /// ... <c>)</c>) from the text immediately preceding a <c>{</c>, returning just the innermost
    /// parenthesized expression — the actual boolean condition, without the surrounding keyword or
    /// its wrapping parens. This matters because <see cref="SplitTopLevelOr"/> and <see
    /// cref="DisjunctHasPositiveGuard"/> reason about paren depth relative to the condition
    /// itself: if the <c>if (</c> wrapper's own opening paren were left in, it would permanently
    /// hold the depth counter at 1 for the whole condition, so a genuine top-level <c>||</c> (at
    /// depth 0 <em>within the condition</em>) would never be recognized as depth 0 and the
    /// disjunction-bypass check in <see cref="ConditionContainsPositiveGuard"/> would silently
    /// stop firing. Finds the innermost condition by matching the LAST <c>)</c> in the (trimmed)
    /// text back to its corresponding <c>(</c>. Text that does not end in <c>)</c> (e.g. a bare
    /// <c>else {</c>, or an unrelated preceding statement with no trailing condition) is returned
    /// unchanged, which safely yields "no guard found" downstream.
    /// </summary>
    private static string ExtractParenthesizedCondition(string text)
    {
        string trimmed = text.TrimEnd();
        if (trimmed.Length == 0 || trimmed[^1] != ')')
        {
            return text;
        }

        int depth = 0;
        for (int idx = trimmed.Length - 1; idx >= 0; idx--)
        {
            char c = trimmed[idx];
            if (c == ')')
            {
                depth++;
            }
            else if (c == '(')
            {
                depth--;
                if (depth == 0)
                {
                    return trimmed[(idx + 1)..^1];
                }
            }
        }

        // Unbalanced parens (shouldn't happen for a real if-condition) — fall back to the
        // original text so downstream logic still runs and conservatively finds no guard.
        return text;
    }

    /// <summary>
    /// Determines whether every occurrence of <c>AuthorizedHubGroups.Farm</c> in
    /// <paramref name="methodBody"/> is nested inside an <c>if</c> block whose condition text
    /// contains a non-negated (see <see cref="ConditionContainsPositiveGuard"/>) call to
    /// <c>HasPermission(</c> or <c>IsFarmAdmin(</c>. Walks the body char-by-char tracking a
    /// brace-depth guard stack: the frame pushed for each <c>{</c> records whether the
    /// immediately preceding (non-brace) text back to the previous <c>{</c>/<c>}</c>/<c>;</c>
    /// contains a positive guard-condition call. A <c>AuthorizedHubGroups.Farm</c> join is
    /// guarded if any frame currently on the stack is a guard frame.
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
                string conditionText = ExtractParenthesizedCondition(precedingText);
                bool isGuardFrame = ConditionContainsPositiveGuard(conditionText);
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

    /// <summary>
    /// Self-test of the scanner's own guard-detection logic (not the repo scan above). Ensures a
    /// positively-gated join is accepted, an ungated join is rejected, a join gated behind a
    /// NEGATED <c>HasPermission(</c>/<c>IsFarmAdmin(</c> check (which recreates the exact
    /// vulnerability this test exists to catch: the join fires only when the caller LACKS the
    /// permission) is rejected, and a top-level <c>||</c> disjunction with a non-guarded
    /// alternative (e.g. <c>HasPermission(...) || true</c>, which would let the join through
    /// regardless of permission) is also rejected, while a disjunction of two genuine alternative
    /// permission checks is still accepted. Each case above was a real gap caught by reviewer
    /// feedback on this test's own first two drafts.
    /// </summary>
    [Theory(DisplayName = "Presubmit: the scanner's own guard detection rejects negated and disjunction-bypassed permission checks")]
    [InlineData(
        "if (PrintFarmerPermissions.HasPermission(Context.User!, HarvestAdminPermission)) { Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm); }",
        true)]
    [InlineData(
        "Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm);",
        false)]
    [InlineData(
        "if (!PrintFarmerPermissions.HasPermission(Context.User!, HarvestAdminPermission)) { Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm); }",
        false)]
    [InlineData(
        "if (!(PrintFarmerPermissions.HasPermission(Context.User!, HarvestAdminPermission))) { Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm); }",
        false)]
    [InlineData(
        "if (PrintFarmerPermissions.IsFarmAdmin(Context.User!)) { Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm); }",
        true)]
    [InlineData(
        "if (PrintFarmerPermissions.HasPermission(Context.User!, HarvestAdminPermission) || true) { Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm); }",
        false)]
    [InlineData(
        "if (PrintFarmerPermissions.HasPermission(Context.User!, \"a\") || PrintFarmerPermissions.HasPermission(Context.User!, \"b\")) { Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm); }",
        true)]
    [InlineData(
        "if (!(someFlag && PrintFarmerPermissions.HasPermission(Context.User!, HarvestAdminPermission))) { Groups.AddToGroupAsync(Context.ConnectionId, AuthorizedHubGroups.Farm); }",
        false)]
    public void GuardDetection_HandlesNegatedAndPositiveConditions(string snippet, bool expectedAllGuarded)
    {
        bool allGuarded = AllFarmGroupJoinsAreGuarded(snippet, out bool referencesFarmGroup);

        Assert.True(referencesFarmGroup, "Test snippet must reference AuthorizedHubGroups.Farm.");
        Assert.Equal(expectedAllGuarded, allGuarded);
    }
}
