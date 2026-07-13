using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddIdempotencyKeyCaseSensitiveCollation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "UserId",
            table: "IdempotencyRecords",
            type: "nvarchar(450)",
            maxLength: 450,
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
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "UserId",
            table: "IdempotencyRecords",
            type: "nvarchar(450)",
            maxLength: 450,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(450)",
            oldMaxLength: 450,
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
    }
}
