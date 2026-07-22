using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddApiKeyPurposeScopesAndExpiry : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Purpose",
            table: "ApiKeys",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "Scopes",
            table: "ApiKeys",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Purpose",
            table: "ApiKeys");

        migrationBuilder.DropColumn(
            name: "Scopes",
            table: "ApiKeys");
    }
}
