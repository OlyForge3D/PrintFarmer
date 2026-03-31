using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddGcodeFileMetadataColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "BottomSolidLayers",
            table: "GcodeFiles",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "FirstLayerHeight",
            table: "GcodeFiles",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IroningEnabled",
            table: "GcodeFiles",
            type: "bit",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "MaxVolumetricSpeed",
            table: "GcodeFiles",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ObjectCount",
            table: "GcodeFiles",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "ObjectDimensionX",
            table: "GcodeFiles",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "ObjectDimensionY",
            table: "GcodeFiles",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "ObjectDimensionZ",
            table: "GcodeFiles",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "RetractionLength",
            table: "GcodeFiles",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "RetractionSpeed",
            table: "GcodeFiles",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "SupportEnabled",
            table: "GcodeFiles",
            type: "bit",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ToolChangesCount",
            table: "GcodeFiles",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TopSolidLayers",
            table: "GcodeFiles",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TotalLayers",
            table: "GcodeFiles",
            type: "int",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BottomSolidLayers",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "FirstLayerHeight",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "IroningEnabled",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "MaxVolumetricSpeed",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "ObjectCount",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "ObjectDimensionX",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "ObjectDimensionY",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "ObjectDimensionZ",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "RetractionLength",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "RetractionSpeed",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "SupportEnabled",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "ToolChangesCount",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "TopSolidLayers",
            table: "GcodeFiles");

        migrationBuilder.DropColumn(
            name: "TotalLayers",
            table: "GcodeFiles");
    }
}
