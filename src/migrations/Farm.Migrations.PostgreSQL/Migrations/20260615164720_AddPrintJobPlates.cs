using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddPrintJobPlates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PlateIndex",
            table: "PrintJobs",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PlateName",
            table: "PrintJobs",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PlateIndex",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "PlateName",
            table: "PrintJobs");
    }
}
