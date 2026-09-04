using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AllowSharedGcodeFilePromotionCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GcodePromotionCheckpoints_GcodeFileId",
                table: "GcodePromotionCheckpoints");

            migrationBuilder.CreateIndex(
                name: "IX_GcodePromotionCheckpoints_GcodeFileId",
                table: "GcodePromotionCheckpoints",
                column: "GcodeFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GcodePromotionCheckpoints_GcodeFileId",
                table: "GcodePromotionCheckpoints");

            migrationBuilder.CreateIndex(
                name: "IX_GcodePromotionCheckpoints_GcodeFileId",
                table: "GcodePromotionCheckpoints",
                column: "GcodeFileId",
                unique: true);
        }
    }
}
