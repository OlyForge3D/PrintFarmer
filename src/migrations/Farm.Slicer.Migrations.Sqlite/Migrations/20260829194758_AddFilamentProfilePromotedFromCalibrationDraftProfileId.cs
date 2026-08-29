using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentProfilePromotedFromCalibrationDraftProfileId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PromotedFromCalibrationDraftProfileId",
                table: "FilamentProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_PromotedFromCalibrationDraftProfileId",
                table: "FilamentProfiles",
                column: "PromotedFromCalibrationDraftProfileId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_PromotedFromCalibrationDraftProfileId",
                table: "FilamentProfiles");

            migrationBuilder.DropColumn(
                name: "PromotedFromCalibrationDraftProfileId",
                table: "FilamentProfiles");
        }
    }
}
