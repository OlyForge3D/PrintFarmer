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
        // Build ancestor → list of (descendant, edgeOnDelete, fkName) tuples for cascading
        // edges only. Intermediate hop: OnDelete must be Cascade. Terminal hop: OnDelete may
        // be Cascade OR SetNull.
        //
        // F5a hardening — include the FK name in the edge tuple so parallel FKs between the
        // same two tables (distinct columns) are enumerated as distinct paths rather than
        // dedup'd by table-string alone.
        Dictionary<string, List<(string Child, ReferentialAction OnDelete, string FkName)>> childrenOf = new();
        foreach ((string child, Dictionary<string, (string, ReferentialAction)> fks) in graph)
        {
            foreach ((string fkName, (string referenced, ReferentialAction onDelete)) in fks)
            {
                if (onDelete != ReferentialAction.Cascade && onDelete != ReferentialAction.SetNull)
                {
                    continue;
                }

                if (!childrenOf.TryGetValue(referenced, out List<(string, ReferentialAction, string)>? bucket))
                {
                    bucket = new List<(string, ReferentialAction, string)>();
                    childrenOf[referenced] = bucket;
                }

                bucket.Add((child, onDelete, fkName));
            }
        }

        List<(string, string, List<string>)> results = new();
        foreach (string ancestor in childrenOf.Keys)
        {
            Dictionary<string, HashSet<string>> pathsTo = new();
            Walk(ancestor, ancestor, new HashSet<string> { ancestor }, childrenOf, pathsTo);
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
    ///
    /// F5a hardening — the walked path is a formatted string that embeds the FK edge label
    /// at each hop, e.g. <c>Printers --[FK_A_Printers_Id]--&gt; A --[FK_B_A_Id]--&gt; B</c>.
    /// Two distinct FKs between the same tables therefore produce distinct paths and are
    /// not dedup'd by <c>HashSet&lt;string&gt;</c> membership.
    /// </summary>
    private static void Walk(
        string current,
        string currentPath,
        HashSet<string> visited,
        Dictionary<string, List<(string Child, ReferentialAction OnDelete, string FkName)>> childrenOf,
        Dictionary<string, HashSet<string>> pathsTo)
    {
        if (!childrenOf.TryGetValue(current, out List<(string Child, ReferentialAction OnDelete, string FkName)>? children))
        {
            return;
        }

        foreach ((string child, ReferentialAction onDelete, string fkName) in children)
        {
            if (visited.Contains(child))
            {
                continue;
            }

            string newPath = $"{currentPath} --[{fkName}]--> {child}";
            if (!pathsTo.TryGetValue(child, out HashSet<string>? paths))
            {
                paths = new HashSet<string>();
                pathsTo[child] = paths;
            }

            _ = paths.Add(newPath);

            // Only Cascade edges continue propagation. SetNull terminates at this hop.
            if (onDelete == ReferentialAction.Cascade)
            {
                _ = visited.Add(child);
                Walk(child, newPath, visited, childrenOf, pathsTo);
                _ = visited.Remove(child);
            }
        }
    }

    // -----------------------------------------------------------------------------------
    // F5b hardening — positive synthetic known-bad graph fixture that exercises the
    // scanner's internal enumeration logic on a hand-built topology. Ensures the scanner
    // detects a 1785-shaped multi-cascading-path from an ancestor to a descendant that
    // is reachable via BOTH a direct Cascade FK AND an indirect Cascade-chain FK — the
    // same shape as the real CameraSnapshots / PartOutputMappings bugs Dallas fixed.
    // -----------------------------------------------------------------------------------

    [Fact]
    public void PositiveFixture_SyntheticKnownBad1785Topology_IsDetectedByEnumeration()
    {
        // Graph: Ancestor → Middle (Cascade via FK_M_A); Ancestor → Descendant (Cascade via FK_D_A_direct);
        //        Middle → Descendant (Cascade via FK_D_M).
        // Expected: Ancestor ⇒ Descendant reachable via 2 distinct cascade paths — must be flagged.
        Dictionary<string, Dictionary<string, (string Referenced, ReferentialAction OnDelete)>> graph = new()
        {
            ["Middle"] = new()
            {
                ["FK_M_A"] = ("Ancestor", ReferentialAction.Cascade),
            },
            ["Descendant"] = new()
            {
                ["FK_D_A_direct"] = ("Ancestor", ReferentialAction.Cascade),
                ["FK_D_M"] = ("Middle", ReferentialAction.Cascade),
            },
        };

        List<(string Ancestor, string Descendant, List<string> Paths)> violations =
            EnumerateMultiCascadingPaths(graph).ToList();

        violations.Should().ContainSingle(
            v => v.Ancestor == "Ancestor" && v.Descendant == "Descendant",
            "Ancestor⇒Descendant is a 1785 topology (direct + via Middle, both Cascade) — must be detected.");
        (string _, string _, List<string> paths) = violations.Single(v => v.Ancestor == "Ancestor" && v.Descendant == "Descendant");
        paths.Should().HaveCount(2, "both direct and indirect paths must appear as distinct entries");
        paths.Should().Contain(p => p.Contains("FK_D_A_direct", StringComparison.Ordinal),
            "direct edge must be present in one path");
        paths.Should().Contain(p => p.Contains("FK_D_M", StringComparison.Ordinal),
            "indirect edge (via Middle) must be present in another path");
    }

    [Fact]
    public void PositiveFixture_SetNullTerminatesPropagation_IsNotFlagged()
    {
        // Graph: Ancestor → Middle (SetNull); Middle → Descendant (Cascade).
        // Per SQL Server's 1785 rule, SetNull at Middle terminates propagation — Descendant
        // is not a cascade target of Ancestor via this chain. The direct Ancestor→Descendant
        // path is the only one that counts.
        Dictionary<string, Dictionary<string, (string Referenced, ReferentialAction OnDelete)>> graph = new()
        {
            ["Middle"] = new()
            {
                ["FK_M_A"] = ("Ancestor", ReferentialAction.SetNull),
            },
            ["Descendant"] = new()
            {
                ["FK_D_M"] = ("Middle", ReferentialAction.Cascade),
            },
        };

        List<(string Ancestor, string Descendant, List<string> Paths)> violations =
            EnumerateMultiCascadingPaths(graph).ToList();

        violations.Should().NotContain(
            v => v.Ancestor == "Ancestor" && v.Descendant == "Descendant",
            "SetNull at Middle terminates propagation — no cascading path from Ancestor to Descendant.");
    }

    [Fact]
    public void PositiveFixture_ParallelFksBetweenSameTables_ProduceDistinctPaths()
    {
        // Graph: A has two distinct FKs pointing at B (col1 Cascade, col2 Cascade).
        // Under a naïve path-by-tables-only scanner, both would produce the same "B → A"
        // string and be dedup'd. F5a hardening ensures they produce two distinct paths.
        Dictionary<string, Dictionary<string, (string Referenced, ReferentialAction OnDelete)>> graph = new()
        {
            ["Ancestor"] = new(),
            ["Middle"] = new()
            {
                ["FK_M_A_col1"] = ("Ancestor", ReferentialAction.Cascade),
                ["FK_M_A_col2"] = ("Ancestor", ReferentialAction.Cascade),
            },
            ["Descendant"] = new()
            {
                ["FK_D_M"] = ("Middle", ReferentialAction.Cascade),
            },
        };

        List<(string Ancestor, string Descendant, List<string> Paths)> violations =
            EnumerateMultiCascadingPaths(graph).ToList();

        (string _, string _, List<string> descendantPaths) = violations.Single(
            v => v.Ancestor == "Ancestor" && v.Descendant == "Descendant");
        descendantPaths.Should().HaveCount(2,
            "two parallel FKs between the same tables must produce two distinct paths (dedup by table alone is a bug — F5a).");
        descendantPaths.Should().Contain(p => p.Contains("FK_M_A_col1", StringComparison.Ordinal));
        descendantPaths.Should().Contain(p => p.Contains("FK_M_A_col2", StringComparison.Ordinal));
    }
}
