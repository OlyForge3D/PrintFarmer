using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Migrations;

/// <summary>
/// Migration-level multi-cascading-path scanner for #953 / #723 (SQL Server error 1785).
///
/// SQL Server's precise 1785 rule: for each new FK, the RDBMS walks backwards from the
/// child table looking for a distinct existing "cascade path" from any common ancestor
/// to that child. A cascade path is a chain of edges where every INTERMEDIATE edge is
/// <c>Cascade</c> and the TERMINAL edge into the child may be <c>Cascade</c> OR
/// <c>SetNull</c>. <c>SetNull</c> as an intermediate edge terminates propagation: once
/// SQL Server nulls out a mid-chain FK, no further deletes/nulls flow from that node.
///
/// The scanner replays every migration in the assembly (in
/// <see cref="MigrationAttribute.Id"/> order) and, after each migration's
/// <c>Up(MigrationBuilder)</c>, recomputes the FK graph and enumerates all first-appearance
/// (ancestor, descendant) pairs reachable via ≥ 2 distinct paths where the paths satisfy
/// the rule above (all-Cascade intermediates + Cascade/SetNull terminal). First-appearance
/// wins so a downstream corrective migration cannot mask an earlier migration that would
/// fail on a fresh SQL Server install.
///
/// Runs against both providers even though PostgreSQL does not reject this at DDL time —
/// PostgreSQL would still fail at DELETE time on identical semantics, so both providers
/// are held to the same standard.
///
/// Roots covered by Dallas's #953 adjudication (each live-probed in a sibling test):
///  1) <c>FK_PrinterMaintenanceSchedules_Printers_PrinterId</c> Cascade → Restrict.
///  2) <c>FK_MaintenanceLogs_MaintenanceAlerts_ResolvedAlertId</c> SetNull → Restrict in InitialV1.
///  3) <c>FK_CameraSnapshots_Cameras_CameraId</c> Cascade → Restrict in
///     <c>AddNozzleDiameterAndHasMmuToPrinter</c>.
///  4) <c>FK_PartOutputMappings_GcodeFiles_GcodeFileId</c> Cascade → Restrict in
///     <c>AddPrintedPartsInventory</c> (with a compensating explicit direct-mapping
///     deletion in <c>GcodeFilesService.DeleteFilesAsync</c>).
/// </summary>
public sealed class MigrationCascadingPathScannerTests
{
    /// <summary>
    /// Known-pre-existing multi-cascading-path pairs that still survive Dallas's full-chain
    /// fix. Each entry must be justified by a linked follow-up. Do NOT expand this list
    /// without coordinator approval. Removing an entry after the underlying migration is
    /// fixed is the intended cleanup path — after Dallas's full-chain adjudication this
    /// list is empty.
    /// </summary>
    private static readonly (string Ancestor, string Descendant, string FirstMigrationPrefix, string Note)[] KnownPreexistingViolations =
    [
    ];

    [Fact]
    public void PostgresMigrations_HaveOnlyKnownPreexistingCascadingPathViolations()
    {
        AssertMigrationChainCascadingPaths(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV1).Assembly,
            activeProvider: "Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    [Fact]
    public void SqlServerMigrations_HaveOnlyKnownPreexistingCascadingPathViolations()
    {
        AssertMigrationChainCascadingPaths(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV1).Assembly,
            activeProvider: "Microsoft.EntityFrameworkCore.SqlServer");
    }

    /// <summary>
    /// Lock-in — SQL Server DOES apply <c>Wave9_BedTypeAndDispatchDefaults</c> at runtime
    /// because its two <c>BedTypes ⇒ MaintenancePlans</c> paths both start with a SetNull
    /// edge (<c>Printers.DefaultBedTypeId</c>, <c>PrinterModels.DefaultBedTypeId</c>), which
    /// terminates propagation. The scanner must NOT flag this per the precise 1785 rule.
    /// </summary>
    [Fact]
    public void Wave9_BedTypesToMaintenancePlans_IsNotFlagged()
    {
        (List<(string, string, string, List<string>)> pgFindings, _) = EnumerateAllFirstAppearanceViolations(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV1).Assembly,
            "Npgsql.EntityFrameworkCore.PostgreSQL");
        (List<(string, string, string, List<string>)> ssFindings, _) = EnumerateAllFirstAppearanceViolations(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV1).Assembly,
            "Microsoft.EntityFrameworkCore.SqlServer");

        pgFindings.Should().NotContain(f => f.Item2 == "BedTypes" && f.Item3 == "MaintenancePlans",
            "SetNull as an intermediate edge (BedTypes → Printers/PrinterModels → MaintenancePlans) "
            + "terminates propagation per SQL Server's 1785 rule.");
        ssFindings.Should().NotContain(f => f.Item2 == "BedTypes" && f.Item3 == "MaintenancePlans",
            "Same rule for SqlServer.");
    }

    /// <summary>
    /// Lock-in — after Dallas Fix 4 the <c>GcodeFiles ⇒ PartOutputMappings</c> two-path
    /// violation must be cleared. If Fix 4 regresses, this test catches it independently
    /// of the main allowlist logic.
    /// </summary>
    [Fact]
    public void PostFix4_GcodeFilesToPartOutputMappings_IsNotFlagged()
    {
        (List<(string, string, string, List<string>)> pgFindings, _) = EnumerateAllFirstAppearanceViolations(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV1).Assembly,
            "Npgsql.EntityFrameworkCore.PostgreSQL");
        (List<(string, string, string, List<string>)> ssFindings, _) = EnumerateAllFirstAppearanceViolations(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV1).Assembly,
            "Microsoft.EntityFrameworkCore.SqlServer");

        pgFindings.Should().NotContain(f => f.Item2 == "GcodeFiles" && f.Item3 == "PartOutputMappings",
            "Fix 4 removed the direct GcodeFile → PartOutputMapping cascade path.");
        ssFindings.Should().NotContain(f => f.Item2 == "GcodeFiles" && f.Item3 == "PartOutputMappings",
            "Fix 4 removed the direct GcodeFile → PartOutputMapping cascade path.");
    }

    /// <summary>
    /// Lock-in — after Dallas Fix 3 the <c>Printers ⇒ CameraSnapshots</c> two-path
    /// violation must be cleared for both providers.
    /// </summary>
    [Fact]
    public void PostFix3_PrintersToCameraSnapshots_IsNotFlagged()
    {
        (List<(string, string, string, List<string>)> pgFindings, _) = EnumerateAllFirstAppearanceViolations(
            typeof(Farm.Migrations.PostgreSQL.Migrations.InitialV1).Assembly,
            "Npgsql.EntityFrameworkCore.PostgreSQL");
        (List<(string, string, string, List<string>)> ssFindings, _) = EnumerateAllFirstAppearanceViolations(
            typeof(Farm.Migrations.SqlServer.Migrations.InitialV1).Assembly,
            "Microsoft.EntityFrameworkCore.SqlServer");

        pgFindings.Should().NotContain(f => f.Item2 == "Printers" && f.Item3 == "CameraSnapshots",
            "Fix 3 removed the CameraSnapshots.CameraId cascade path.");
        ssFindings.Should().NotContain(f => f.Item2 == "Printers" && f.Item3 == "CameraSnapshots",
            "Fix 3 removed the CameraSnapshots.CameraId cascade path.");
    }

    private static void AssertMigrationChainCascadingPaths(Assembly migrationAssembly, string activeProvider)
    {
        (List<(string MigrationId, string Ancestor, string Descendant, List<string> Paths)> firstAppearances, int migrationCount) =
            EnumerateAllFirstAppearanceViolations(migrationAssembly, activeProvider);
        migrationCount.Should().BeGreaterThan(0, "migration assembly must contain at least InitialV1");

        List<string> regressions = new();
        foreach ((string migrationId, string ancestor, string descendant, List<string> paths) in firstAppearances)
        {
            bool allowed = KnownPreexistingViolations.Any(entry =>
                entry.Ancestor == ancestor
                && entry.Descendant == descendant
                && migrationId.StartsWith(entry.FirstMigrationPrefix, StringComparison.Ordinal));
            string summary = $"{ancestor} ⇒ {descendant} (first at {migrationId}): {string.Join(" | ", paths)}";
            if (!allowed)
            {
                regressions.Add(summary);
            }
        }

        regressions.Should().BeEmpty(
            "SQL Server 1785 fires when a table has ≥ 2 distinct cascade paths (all-Cascade "
            + "intermediates, Cascade/SetNull terminal) from the same ancestor. Any listed pair "
            + "is a new regression not in the KnownPreexistingViolations allowlist. Fix at the "
            + "root or expand the allowlist with a linked follow-up issue (do not silently widen).");
    }

    private static (List<(string MigrationId, string Ancestor, string Descendant, List<string> Paths)> Findings, int MigrationCount)
        EnumerateAllFirstAppearanceViolations(Assembly migrationAssembly, string activeProvider)
    {
        List<(string Id, Type Type)> ordered = new();
        foreach (Type t in migrationAssembly.GetTypes())
        {
            if (!t.IsSubclassOf(typeof(Migration)) || t.IsAbstract)
            {
                continue;
            }

            var attr = t.GetCustomAttribute<MigrationAttribute>();
            if (attr == null)
            {
                continue;
            }

            ordered.Add((attr.Id, t));
        }

        ordered.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        Dictionary<string, Dictionary<string, (string Referenced, ReferentialAction OnDelete)>> graph = new();
        HashSet<(string, string)> seen = new();
        List<(string, string, string, List<string>)> firstAppearances = new();

        foreach ((string id, Type type) in ordered)
        {
            var migration = (Migration)Activator.CreateInstance(type)!;
            var builder = new MigrationBuilder(activeProvider);
            MethodInfo up = type.GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _ = up.Invoke(migration, [builder]);

            ApplyOperations(builder.Operations, graph);

            foreach ((string ancestor, string descendant, List<string> paths) in EnumerateMultiCascadingPaths(graph))
            {
                if (seen.Add((ancestor, descendant)))
                {
                    firstAppearances.Add((id, ancestor, descendant, paths));
                }
            }
        }

        return (firstAppearances, ordered.Count);
    }

    private static void ApplyOperations(
        IReadOnlyList<MigrationOperation> operations,
        Dictionary<string, Dictionary<string, (string Referenced, ReferentialAction OnDelete)>> graph)
    {
        foreach (MigrationOperation op in operations)
        {
            switch (op)
            {
                case CreateTableOperation ct:
                    Dictionary<string, (string, ReferentialAction)> fks = new();
                    foreach (AddForeignKeyOperation fk in ct.ForeignKeys)
                    {
                        fks[fk.Name!] = (fk.PrincipalTable!, fk.OnDelete);
                    }

                    graph[ct.Name] = fks;
                    break;
                case DropTableOperation dt:
                    _ = graph.Remove(dt.Name);
                    break;
                case AddForeignKeyOperation add:
                    if (!graph.TryGetValue(add.Table, out Dictionary<string, (string, ReferentialAction)>? existing))
                    {
                        existing = new Dictionary<string, (string, ReferentialAction)>();
                        graph[add.Table] = existing;
                    }

                    existing[add.Name!] = (add.PrincipalTable!, add.OnDelete);
                    break;
                case DropForeignKeyOperation drop:
                    if (graph.TryGetValue(drop.Table, out Dictionary<string, (string, ReferentialAction)>? existingDrop))
                    {
                        _ = existingDrop.Remove(drop.Name!);
                    }

                    break;
                case RenameTableOperation rn:
                    if (rn.NewName != null && graph.TryGetValue(rn.Name!, out Dictionary<string, (string, ReferentialAction)>? old))
                    {
                        _ = graph.Remove(rn.Name!);
                        graph[rn.NewName] = old;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<(string Ancestor, string Descendant, List<string> Paths)> EnumerateMultiCascadingPaths(
        Dictionary<string, Dictionary<string, (string Referenced, ReferentialAction OnDelete)>> graph)
    {
        // Build ancestor → list of (descendant, edgeOnDelete) pairs for cascading edges only.
        // Intermediate hop: OnDelete must be Cascade. Terminal hop: OnDelete may be Cascade OR SetNull.
        Dictionary<string, List<(string Child, ReferentialAction OnDelete)>> childrenOf = new();
        foreach ((string child, Dictionary<string, (string, ReferentialAction)> fks) in graph)
        {
            foreach ((string referenced, ReferentialAction onDelete) in fks.Values)
            {
                if (onDelete != ReferentialAction.Cascade && onDelete != ReferentialAction.SetNull)
                {
                    continue;
                }

                if (!childrenOf.TryGetValue(referenced, out List<(string, ReferentialAction)>? bucket))
                {
                    bucket = new List<(string, ReferentialAction)>();
                    childrenOf[referenced] = bucket;
                }

                bucket.Add((child, onDelete));
            }
        }

        List<(string, string, List<string>)> results = new();
        foreach (string ancestor in childrenOf.Keys)
        {
            Dictionary<string, HashSet<string>> pathsTo = new();
            Walk(ancestor, new List<string> { ancestor }, new HashSet<string> { ancestor }, childrenOf, pathsTo);
            foreach ((string descendant, HashSet<string> paths) in pathsTo)
            {
                if (paths.Count >= 2)
                {
                    results.Add((ancestor, descendant, paths.OrderBy(p => p, StringComparer.Ordinal).ToList()));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Walks the graph from <paramref name="current"/> to descendants via cascading edges.
    /// A terminal edge (Cascade OR SetNull) records the descendant as a target. Only Cascade
    /// edges recurse further; SetNull terminates propagation at that hop.
    /// </summary>
    private static void Walk(
        string current,
        List<string> path,
        HashSet<string> visited,
        Dictionary<string, List<(string Child, ReferentialAction OnDelete)>> childrenOf,
        Dictionary<string, HashSet<string>> pathsTo)
    {
        if (!childrenOf.TryGetValue(current, out List<(string Child, ReferentialAction OnDelete)>? children))
        {
            return;
        }

        foreach ((string child, ReferentialAction onDelete) in children)
        {
            if (visited.Contains(child))
            {
                continue;
            }

            List<string> newPath = new(path) { child };
            if (!pathsTo.TryGetValue(child, out HashSet<string>? paths))
            {
                paths = new HashSet<string>();
                pathsTo[child] = paths;
            }

            _ = paths.Add(string.Join(" → ", newPath));

            // Only Cascade edges continue propagation. SetNull terminates at this hop.
            if (onDelete == ReferentialAction.Cascade)
            {
                _ = visited.Add(child);
                Walk(child, newPath, visited, childrenOf, pathsTo);
                _ = visited.Remove(child);
            }
        }
    }
}

