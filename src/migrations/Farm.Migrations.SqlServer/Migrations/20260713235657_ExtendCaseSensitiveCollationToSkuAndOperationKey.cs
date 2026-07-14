using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class ExtendCaseSensitiveCollationToSkuAndOperationKey : Migration
{
    // Byte-exact, culture-invariant, case-sensitive collation applied by this migration so the
    // store compares these identity/idempotency columns the same way the application does after
    // NFKC folding (issue #715, Frost r6).
    private const string BinaryCaseSensitiveCollation = "Latin1_General_100_BIN2";

    // The collation these columns carried before the BIN2 rewrite. They inherited the database
    // default, which on a typical deployment is SQL Server's default catalog collation
    // SQL_Latin1_General_CP1_CI_AS (confirmed by AppDbContext / CaseSensitiveCollationModelTests).
    // Down() must name it EXPLICITLY: EF's oldCollation: parameter is metadata-only and emits no
    // COLLATE clause, so relying on it would leave the columns on BIN2 and make the rollback a
    // silent no-op (issue #715, Hicks r7 blocker H1).
    private const string DefaultCatalogCollation = "SQL_Latin1_General_CP1_CI_AS";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // === Ledger tables — online-aware index rebuild (issue #715, Vasquez r7 blocker V2) ======
        // PartInventoryAdjustments.OperationKey and PrintJobs.HarvestOperationKey each back a
        // FILTERED UNIQUE index. EF's AlterColumn rewrites the collation by dropping and recreating
        // that index OFFLINE, taking a long write-blocking Sch-M lock on ledgers that can be large
        // in production. We take manual control (raw DROP INDEX -> ALTER COLUMN COLLATE ->
        // CREATE INDEX) so the recreate can run ONLINE where the engine supports it. The filtered
        // predicate is reproduced byte-for-byte so the resulting index still matches the model
        // snapshot; converting these two AlterColumn calls to raw SQL does not change the model, so
        // both providers' has-pending-model-changes checks stay clean.
        migrationBuilder.Sql("DROP INDEX [IX_PrintJobs_HarvestOperationKey] ON [PrintJobs];");
        migrationBuilder.Sql(
            "ALTER TABLE [PrintJobs] ALTER COLUMN [HarvestOperationKey] nvarchar(128) "
            + "COLLATE " + BinaryCaseSensitiveCollation + " NULL;");
        migrationBuilder.Sql(OnlineAwareCreateUniqueIndex(
            indexName: "IX_PrintJobs_HarvestOperationKey",
            table: "PrintJobs",
            columnList: "[HarvestOperationKey]",
            filterPredicate: "[HarvestOperationKey] IS NOT NULL"));

        migrationBuilder.Sql(
            "DROP INDEX [IX_PartInventoryAdjustments_PartInventoryId_OperationKey] "
            + "ON [PartInventoryAdjustments];");
        migrationBuilder.Sql(
            "ALTER TABLE [PartInventoryAdjustments] ALTER COLUMN [OperationKey] nvarchar(128) "
            + "COLLATE " + BinaryCaseSensitiveCollation + " NULL;");
        migrationBuilder.Sql(OnlineAwareCreateUniqueIndex(
            indexName: "IX_PartInventoryAdjustments_PartInventoryId_OperationKey",
            table: "PartInventoryAdjustments",
            columnList: "[PartInventoryId], [OperationKey]",
            filterPredicate: "[OperationKey] IS NOT NULL"));

        // === Identity tables with dependent CHECK constraints ====================================
        // Sku and Code each have a CK_..._Normalized CHECK constraint referencing the column, so
        // SQL Server forbids ALTER COLUMN ... COLLATE while the constraint exists (Msg 5074). Drop
        // the constraint, let EF alter the collation (EF still auto-rebuilds the dependent unique
        // index — OFFLINE, which is acceptable and left as-is per V2 scope: these are small identity
        // tables, not the high-volume ledgers), then recreate the constraint.
        //
        // The constraint is recreated WITH NOCHECK (issue #715, Vasquez r7 blocker V1). Under the
        // old CI_AS collation, [Sku] = UPPER([Sku]) was effectively always true, but under BIN2 it
        // now enforces byte-exact upper-case; a validating ADD CONSTRAINT would table-scan under a
        // Sch-M lock and roll back (Msg 547) on any legacy non-upper-case row. WITH NOCHECK is
        // instant/metadata-only: existing rows are grandfathered while every future INSERT/UPDATE is
        // still enforced. This is safe because (a) the feature is <1 week old, so there is no
        // meaningful legacy data, and (b) the only write path — PartInventoryIdentity.NormalizeSku /
        // NormalizeBinCode — always ToUpperInvariant()s before persistence, so no compliant writer
        // can ever emit a violating row.
        migrationBuilder.Sql(
            "ALTER TABLE [PartInventories] DROP CONSTRAINT IF EXISTS [CK_PartInventories_Sku_Normalized];");

        migrationBuilder.AlterColumn<string>(
            name: "Sku",
            table: "PartInventories",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: false,
            collation: BinaryCaseSensitiveCollation,
            oldClrType: typeof(string),
            oldType: "nvarchar(64)",
            oldMaxLength: 64);

        migrationBuilder.Sql(
            "ALTER TABLE [PartInventories] WITH NOCHECK ADD CONSTRAINT [CK_PartInventories_Sku_Normalized] "
            + "CHECK ([Sku] = UPPER([Sku]));");

        migrationBuilder.Sql(
            "ALTER TABLE [Bins] DROP CONSTRAINT IF EXISTS [CK_Bins_Code_Normalized];");

        migrationBuilder.AlterColumn<string>(
            name: "Code",
            table: "Bins",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: false,
            collation: BinaryCaseSensitiveCollation,
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128);

        migrationBuilder.Sql(
            "ALTER TABLE [Bins] WITH NOCHECK ADD CONSTRAINT [CK_Bins_Code_Normalized] "
            + "CHECK ([Code] = UPPER([Code]));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restore the original SQL_Latin1_General_CP1_CI_AS collation EXPLICITLY on every column
        // (issue #715, Hicks r7 blocker H1); EF's oldCollation: metadata alone emits no COLLATE and
        // would leave the columns on BIN2. WARNING: rollback can legitimately fail if BIN2 admitted
        // rows that CI_AS treats as duplicates under the unique index (values differing only by
        // case/width) — an inherent risk of widening a collation, surfaced as an error rather than
        // hidden. The ledger index rebuilds stay online-aware (see OnlineAwareCreateUniqueIndex).
        migrationBuilder.Sql("DROP INDEX [IX_PrintJobs_HarvestOperationKey] ON [PrintJobs];");
        migrationBuilder.Sql(
            "ALTER TABLE [PrintJobs] ALTER COLUMN [HarvestOperationKey] nvarchar(128) "
            + "COLLATE " + DefaultCatalogCollation + " NULL;");
        migrationBuilder.Sql(OnlineAwareCreateUniqueIndex(
            indexName: "IX_PrintJobs_HarvestOperationKey",
            table: "PrintJobs",
            columnList: "[HarvestOperationKey]",
            filterPredicate: "[HarvestOperationKey] IS NOT NULL"));

        migrationBuilder.Sql(
            "DROP INDEX [IX_PartInventoryAdjustments_PartInventoryId_OperationKey] "
            + "ON [PartInventoryAdjustments];");
        migrationBuilder.Sql(
            "ALTER TABLE [PartInventoryAdjustments] ALTER COLUMN [OperationKey] nvarchar(128) "
            + "COLLATE " + DefaultCatalogCollation + " NULL;");
        migrationBuilder.Sql(OnlineAwareCreateUniqueIndex(
            indexName: "IX_PartInventoryAdjustments_PartInventoryId_OperationKey",
            table: "PartInventoryAdjustments",
            columnList: "[PartInventoryId], [OperationKey]",
            filterPredicate: "[OperationKey] IS NOT NULL"));

        // Mirror the CHECK-constraint drop/recreate around the collation revert for Sku/Code, again
        // recreating WITH NOCHECK so the reverted (now CI_AS) constraint never validation-scans.
        migrationBuilder.Sql(
            "ALTER TABLE [PartInventories] DROP CONSTRAINT IF EXISTS [CK_PartInventories_Sku_Normalized];");

        migrationBuilder.AlterColumn<string>(
            name: "Sku",
            table: "PartInventories",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: false,
            collation: DefaultCatalogCollation,
            oldClrType: typeof(string),
            oldType: "nvarchar(64)",
            oldMaxLength: 64,
            oldCollation: BinaryCaseSensitiveCollation);

        migrationBuilder.Sql(
            "ALTER TABLE [PartInventories] WITH NOCHECK ADD CONSTRAINT [CK_PartInventories_Sku_Normalized] "
            + "CHECK ([Sku] = UPPER([Sku]));");

        migrationBuilder.Sql(
            "ALTER TABLE [Bins] DROP CONSTRAINT IF EXISTS [CK_Bins_Code_Normalized];");

        migrationBuilder.AlterColumn<string>(
            name: "Code",
            table: "Bins",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: false,
            collation: DefaultCatalogCollation,
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128,
            oldCollation: BinaryCaseSensitiveCollation);

        migrationBuilder.Sql(
            "ALTER TABLE [Bins] WITH NOCHECK ADD CONSTRAINT [CK_Bins_Code_Normalized] "
            + "CHECK ([Code] = UPPER([Code]));");
    }

    // Emits a filtered UNIQUE index CREATE whose ONLINE option is chosen at runtime from the
    // server's engine edition (issue #715, Vasquez r7 blocker V2). Online index CREATE is supported
    // only on EngineEdition 3 (Enterprise / Developer / Evaluation), 5 (Azure SQL Database), and
    // 8 (Azure SQL Managed Instance). On Standard edition (EngineEdition = 2) specifying
    // WITH (ONLINE = ON) FAILS with a hard error ("Online index operations are not supported in
    // this edition of SQL Server") — it is NOT silently ignored — so we branch and fall back to
    // ONLINE = OFF there. That keeps the migration deployable on every edition while still avoiding
    // write-blocking rebuilds wherever the engine allows it. Names are compile-time constants from
    // this migration (no user input), so the dynamic SQL carries no injection surface. MAXDOP = 0
    // lets the engine choose the rebuild parallelism.
    private static string OnlineAwareCreateUniqueIndex(
        string indexName,
        string table,
        string columnList,
        string filterPredicate)
        => "DECLARE @online nvarchar(3) = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) "
            + "IN (3, 5, 8) THEN N'ON' ELSE N'OFF' END;\n"
            + "DECLARE @sql nvarchar(max) = N'CREATE UNIQUE NONCLUSTERED INDEX [" + indexName + "] "
            + "ON [" + table + "] (" + columnList + ") WHERE " + filterPredicate + " "
            + "WITH (ONLINE = ' + @online + N', MAXDOP = 0);';\n"
            + "EXEC sys.sp_executesql @sql;";
}
