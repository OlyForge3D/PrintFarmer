using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddCatalogUpdateTracking : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "LastModelSyncAt",
            table: "Printers",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "UpdatedAt",
            table: "PrinterModels",
            type: "datetime2",
            nullable: false,
            defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

        migrationBuilder.CreateTable(
            name: "CatalogVersions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Version = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ManifestHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                AppliedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Source = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogVersions", x => x.Id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CatalogVersions");

        migrationBuilder.DropColumn(
            name: "LastModelSyncAt",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "UpdatedAt",
            table: "PrinterModels");
    }
}
