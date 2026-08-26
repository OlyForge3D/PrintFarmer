using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomProfileFamilyRenderingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OverridesJson",
                table: "MachineProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSystemPresetName",
                table: "MachineProfiles",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "MachineModelProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FamilyOverridesJson",
                table: "MachineModelProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRenderedAt",
                table: "MachineModelProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenderStatus",
                table: "MachineModelProfiles",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValueSql: "'NotApplicable'");

            migrationBuilder.AddColumn<string>(
                name: "RenderedForOrcaVersion",
                table: "MachineModelProfiles",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlicerDistribution",
                table: "MachineModelProfiles",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceMachineModelName",
                table: "MachineModelProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineModelProfiles_CreatedByUserId",
                table: "MachineModelProfiles",
                column: "CreatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MachineModelProfiles_CreatedByUserId",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "OverridesJson",
                table: "MachineProfiles");

            migrationBuilder.DropColumn(
                name: "SourceSystemPresetName",
                table: "MachineProfiles");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "FamilyOverridesJson",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "LastRenderedAt",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "RenderStatus",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "RenderedForOrcaVersion",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "SlicerDistribution",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "SourceMachineModelName",
                table: "MachineModelProfiles");
        }
    }
}
