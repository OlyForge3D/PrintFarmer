using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddIdempotencyKeyCaseSensitiveCollation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQL Server rejects ALTER COLUMN collation (or length) changes on columns
        // that participate in an index (Msg 5074/4922). All three key columns back the
        // composite unique index IX_IdempotencyRecords_User_Route_Key, so it must be
        // dropped before the column rewrites and recreated afterwards. While the index
        // is down we also narrow UserId 450 -> 256: the 450+200+200 nvarchar key was
        // exactly the 1700-byte SQL Server index-key limit with zero headroom.
        migrationBuilder.DropIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords");

        migrationBuilder.AlterColumn<string>(
            name: "UserId",
            table: "IdempotencyRecords",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            collation: "Latin1_General_100_BIN2",
            oldClrType: typeof(string),
            oldType: "nvarchar(450)",
            oldMaxLength: 450);

        migrationBuilder.AlterColumn<string>(
            name: "RouteKey",
            table: "IdempotencyRecords",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: false,
            collation: "Latin1_General_100_BIN2",
            oldClrType: typeof(string),
            oldType: "nvarchar(200)",
            oldMaxLength: 200);

        migrationBuilder.AlterColumn<string>(
            name: "IdempotencyKey",
            table: "IdempotencyRecords",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: false,
            collation: "Latin1_General_100_BIN2",
            oldClrType: typeof(string),
            oldType: "nvarchar(200)",
            oldMaxLength: 200);

        migrationBuilder.CreateIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords",
            columns: new[] { "UserId", "RouteKey", "IdempotencyKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restore each reverted column to the database's CURRENT catalog collation, read at rollback
        // time via DATABASEPROPERTYEX(DB_NAME(),'Collation') (see RevertCollationToCatalogDefault),
        // instead of the r7 hardcoded SQL_Latin1_General_CP1_CI_AS. A deployment whose catalog
        // collation differs would otherwise be re-collated to the WRONG collation, silently corrupting
        // rollback (issue #715, Hicks r8 blocker H1b; supersedes the r7 hardcoded approach). EF's
        // oldCollation: metadata alone emits no COLLATE, so the revert must be explicit; the columns
        // also widen back UserId 256 -> 450. The composite unique index is dropped and recreated
        // around the rewrites (below), so the ALTERs run with no live index dependency. WARNING: this
        // rollback can legitimately fail if the BIN2 columns admitted rows the target collation's
        // case/width-insensitive unique index treats as duplicates — an inherent risk of widening a
        // collation under a UNIQUE index, surfaced as an error rather than hidden.
        migrationBuilder.DropIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords");

        migrationBuilder.Sql(RevertCollationToCatalogDefault(
            table: "IdempotencyRecords",
            column: "UserId",
            columnType: "nvarchar(450)",
            nullability: "NOT NULL"));

        migrationBuilder.Sql(RevertCollationToCatalogDefault(
            table: "IdempotencyRecords",
            column: "RouteKey",
            columnType: "nvarchar(200)",
            nullability: "NOT NULL"));

        migrationBuilder.Sql(RevertCollationToCatalogDefault(
            table: "IdempotencyRecords",
            column: "IdempotencyKey",
            columnType: "nvarchar(200)",
            nullability: "NOT NULL"));

        migrationBuilder.CreateIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords",
            columns: new[] { "UserId", "RouteKey", "IdempotencyKey" },
            unique: true);
    }

    // Reverts a column to the database's CURRENT catalog collation, captured AT ROLLBACK TIME via
    // DATABASEPROPERTYEX(DB_NAME(), 'Collation') — NOT a collation hardcoded when this migration was
    // authored (issue #715, Hicks r8 blocker H1b). A COLLATE clause requires a literal collation
    // identifier and cannot take a function call inline, so the collation is read into a local and
    // the ALTER is issued via sp_executesql. DATABASEPROPERTYEX returns a system collation name
    // constrained to [A-Za-z0-9_] (no injection surface) and is used as a BARE identifier in COLLATE,
    // so it must NOT be wrapped in QUOTENAME (COLLATE rejects a bracketed name). The @coll/@sql
    // locals are suffixed with table+column so repeated calls in one GO batch never redeclare them
    // (Msg 134): `dotnet ef migrations script` concatenates every Sql() call into a single batch, and
    // T-SQL variables are batch-scoped, not block-scoped, so BEGIN/END cannot isolate them.
    private static string RevertCollationToCatalogDefault(
        string table,
        string column,
        string columnType,
        string nullability)
    {
        string suffix = table + "_" + column;
        string coll = "@coll_" + suffix;
        string sql = "@sql_" + suffix;
        return "DECLARE " + coll + " sysname = CAST(DATABASEPROPERTYEX(DB_NAME(), N'Collation') AS sysname);\n"
            + "DECLARE " + sql + " nvarchar(max) = N'ALTER TABLE [" + table + "] ALTER COLUMN [" + column + "] "
            + columnType + " COLLATE ' + " + coll + " + N' " + nullability + ";';\n"
            + "EXEC sys.sp_executesql " + sql + ";";
    }
}
