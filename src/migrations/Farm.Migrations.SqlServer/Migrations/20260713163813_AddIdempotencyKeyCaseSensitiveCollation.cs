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
        // Restore the original default catalog collation EXPLICITLY on every reverted column
        // (issue #715, Hicks r7 blocker H1). EF's oldCollation: parameter is metadata-only and emits
        // no COLLATE clause, so without naming SQL_Latin1_General_CP1_CI_AS here the columns would
        // stay on Latin1_General_100_BIN2 and Down() would be a silent no-op for collation. WARNING:
        // this rollback can legitimately fail if the BIN2 columns admitted rows that the
        // case/width-insensitive CI_AS unique index would treat as duplicates — an inherent risk of
        // widening a collation under a UNIQUE index, surfaced as an error rather than hidden.
        migrationBuilder.DropIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords");

        migrationBuilder.AlterColumn<string>(
            name: "UserId",
            table: "IdempotencyRecords",
            type: "nvarchar(450)",
            maxLength: 450,
            nullable: false,
            collation: "SQL_Latin1_General_CP1_CI_AS",
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256,
            oldCollation: "Latin1_General_100_BIN2");

        migrationBuilder.AlterColumn<string>(
            name: "RouteKey",
            table: "IdempotencyRecords",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: false,
            collation: "SQL_Latin1_General_CP1_CI_AS",
            oldClrType: typeof(string),
            oldType: "nvarchar(200)",
            oldMaxLength: 200,
            oldCollation: "Latin1_General_100_BIN2");

        migrationBuilder.AlterColumn<string>(
            name: "IdempotencyKey",
            table: "IdempotencyRecords",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: false,
            collation: "SQL_Latin1_General_CP1_CI_AS",
            oldClrType: typeof(string),
            oldType: "nvarchar(200)",
            oldMaxLength: 200,
            oldCollation: "Latin1_General_100_BIN2");

        migrationBuilder.CreateIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords",
            columns: new[] { "UserId", "RouteKey", "IdempotencyKey" },
            unique: true);
    }
}
