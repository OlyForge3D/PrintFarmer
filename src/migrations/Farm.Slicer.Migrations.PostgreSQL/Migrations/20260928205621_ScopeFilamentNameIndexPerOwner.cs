using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ScopeFilamentNameIndexPerOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType",
                schema: "slicer",
                table: "FilamentProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_CreatedByUserId_Name_Material_SlicerType",
                schema: "slicer",
                table: "FilamentProfiles",
                columns: new[] { "CreatedByUserId", "Name", "Material", "SlicerType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType_Unowned",
                schema: "slicer",
                table: "FilamentProfiles",
                columns: new[] { "Name", "Material", "SlicerType" },
                unique: true,
                filter: "\"CreatedByUserId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_CreatedByUserId_Name_Material_SlicerType",
                schema: "slicer",
                table: "FilamentProfiles");

            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType_Unowned",
                schema: "slicer",
                table: "FilamentProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType",
                schema: "slicer",
                table: "FilamentProfiles",
                columns: new[] { "Name", "Material", "SlicerType" },
                unique: true);
        }
    }
}
