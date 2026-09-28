using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ScopeFilamentPromotionIndexPerOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_PromotedFromCalibrationDraftProfileId",
                table: "FilamentProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_CreatedByUserId_PromotedFromCalibrationDraftProfileId",
                table: "FilamentProfiles",
                columns: new[] { "CreatedByUserId", "PromotedFromCalibrationDraftProfileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_CreatedByUserId_PromotedFromCalibrationDraftProfileId",
                table: "FilamentProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_PromotedFromCalibrationDraftProfileId",
                table: "FilamentProfiles",
                column: "PromotedFromCalibrationDraftProfileId",
                unique: true);
        }
    }
}
