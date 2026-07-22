using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddModelFileTransformsJson : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ModelFileTransformsJson",
            schema: "slicer",
            table: "SliceJobs",
            type: "nvarchar(max)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ModelFileTransformsJson",
            schema: "slicer",
            table: "SliceJobs");
    }
}
