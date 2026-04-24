using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddExtractedMetadataAndExtruderFilament : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExtruderFilamentProfileNamesJson",
            schema: "slicer",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExtractedMetadataJson",
            schema: "slicer",
            table: "Models3D",
            type: "nvarchar(max)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ExtruderFilamentProfileNamesJson",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ExtractedMetadataJson",
            schema: "slicer",
            table: "Models3D");
    }
}
