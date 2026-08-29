using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentProfilePromotedFromCalibrationDraftProfileId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PromotedFromCalibrationDraftProfileId",
                schema: "slicer",
                table: "FilamentProfiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_PromotedFromCalibrationDraftProfileId",
                schema: "slicer",
                table: "FilamentProfiles",
                column: "PromotedFromCalibrationDraftProfileId",
                unique: true,
                filter: "[PromotedFromCalibrationDraftProfileId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_PromotedFromCalibrationDraftProfileId",
                schema: "slicer",
                table: "FilamentProfiles");

            migrationBuilder.DropColumn(
                name: "PromotedFromCalibrationDraftProfileId",
                schema: "slicer",
                table: "FilamentProfiles");
        }
    }
}
