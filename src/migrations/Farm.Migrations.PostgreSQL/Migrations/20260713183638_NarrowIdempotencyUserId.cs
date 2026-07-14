using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class NarrowIdempotencyUserId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "UserId",
            table: "IdempotencyRecords",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(450)",
            oldMaxLength: 450);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "UserId",
            table: "IdempotencyRecords",
            type: "character varying(450)",
            maxLength: 450,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256);
    }
}
