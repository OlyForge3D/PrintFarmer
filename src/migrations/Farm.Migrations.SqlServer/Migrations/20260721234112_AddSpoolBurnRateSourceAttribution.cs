using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddSpoolBurnRateSourceAttribution : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsFilamentUsageAuthoritative",
            table: "PrintJobToolheadUsages",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "SpoolSourceIdentity",
            table: "PrintJobToolheadUsages",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SpoolSourceKind",
            table: "PrintJobToolheadUsages",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobToolheadUsages_SpoolProjection",
            table: "PrintJobToolheadUsages",
            columns: new[] { "SpoolSourceKind", "SpoolSourceIdentity", "SpoolmanSpoolId", "IsFilamentUsageAuthoritative" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PrintJobToolheadUsages_SpoolProjection",
            table: "PrintJobToolheadUsages");

        migrationBuilder.DropColumn(
            name: "IsFilamentUsageAuthoritative",
            table: "PrintJobToolheadUsages");

        migrationBuilder.DropColumn(
            name: "SpoolSourceIdentity",
            table: "PrintJobToolheadUsages");

        migrationBuilder.DropColumn(
            name: "SpoolSourceKind",
            table: "PrintJobToolheadUsages");
    }
}
