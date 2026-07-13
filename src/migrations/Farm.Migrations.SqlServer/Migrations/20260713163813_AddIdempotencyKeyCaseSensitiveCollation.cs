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
        migrationBuilder.DropIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords");

        migrationBuilder.AlterColumn<string>(
            name: "UserId",
            table: "IdempotencyRecords",
            type: "nvarchar(450)",
            maxLength: 450,
            nullable: false,
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
