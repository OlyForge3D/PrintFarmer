using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddGcodeFilePerExtruderColorAndType : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FilamentPerExtruderColorHex",
            table: "GcodeFiles",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FilamentPerExtruderType",
            table: "GcodeFiles",
            type: "text",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FilamentPerExtruderColorHex",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "FilamentPerExtruderType",
            table: "GcodeFiles");
    }
}
