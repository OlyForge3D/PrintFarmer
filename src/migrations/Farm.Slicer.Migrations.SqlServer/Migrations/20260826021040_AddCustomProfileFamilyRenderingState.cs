using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomProfileFamilyRenderingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OverridesJson",
                schema: "slicer",
                table: "MachineProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSystemPresetName",
                schema: "slicer",
                table: "MachineProfiles",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FamilyOverridesJson",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRenderedAt",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenderStatus",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValueSql: "'NotApplicable'");

            migrationBuilder.AddColumn<string>(
                name: "RenderedForOrcaVersion",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlicerDistribution",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceMachineModelName",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineModelProfiles_CreatedByUserId",
                schema: "slicer",
                table: "MachineModelProfiles",
                column: "CreatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MachineModelProfiles_CreatedByUserId",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "OverridesJson",
                schema: "slicer",
                table: "MachineProfiles");

            migrationBuilder.DropColumn(
                name: "SourceSystemPresetName",
                schema: "slicer",
                table: "MachineProfiles");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "FamilyOverridesJson",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "LastRenderedAt",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "RenderStatus",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "RenderedForOrcaVersion",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "SlicerDistribution",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "SourceMachineModelName",
                schema: "slicer",
                table: "MachineModelProfiles");
        }
    }
}
