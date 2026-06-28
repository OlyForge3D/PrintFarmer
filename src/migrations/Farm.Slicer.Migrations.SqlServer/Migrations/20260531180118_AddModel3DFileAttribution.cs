using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddModel3DFileAttribution : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "ImportedAt",
            schema: "slicer",
            table: "Models3D",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceCreator",
            schema: "slicer",
            table: "Models3D",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceLicense",
            schema: "slicer",
            table: "Models3D",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceUrl",
            schema: "slicer",
            table: "Models3D",
            type: "nvarchar(2048)",
            maxLength: 2048,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ImportedAt",
            schema: "slicer",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "SourceCreator",
            schema: "slicer",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "SourceLicense",
            schema: "slicer",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "SourceUrl",
            schema: "slicer",
            table: "Models3D");
    }
}
