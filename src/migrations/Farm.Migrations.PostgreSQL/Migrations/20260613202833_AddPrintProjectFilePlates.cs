using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddPrintProjectFilePlates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId",
            table: "PrintProjectFiles");

        migrationBuilder.AddColumn<int>(
            name: "PlateIndex",
            table: "PrintProjectFiles",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PlateName",
            table: "PrintProjectFiles",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId_PlateIndex",
            table: "PrintProjectFiles",
            columns: new[] { "PrintProjectId", "GcodeFileId", "PlateIndex" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId_PlateIndex",
            table: "PrintProjectFiles");

        migrationBuilder.DropColumn(
            name: "PlateIndex",
            table: "PrintProjectFiles");

        migrationBuilder.DropColumn(
            name: "PlateName",
            table: "PrintProjectFiles");

        migrationBuilder.CreateIndex(
            name: "IX_PrintProjectFiles_ProjectId_GcodeFileId",
            table: "PrintProjectFiles",
            columns: new[] { "PrintProjectId", "GcodeFileId" },
            unique: true);
    }
}
