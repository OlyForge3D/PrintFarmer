using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ScopeFilamentNameIndexPerOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType",
                table: "FilamentProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_CreatedByUserId_Name_Material_SlicerType",
                table: "FilamentProfiles",
                columns: new[] { "CreatedByUserId", "Name", "Material", "SlicerType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType_Unowned",
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
                table: "FilamentProfiles");

            migrationBuilder.DropIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType_Unowned",
                table: "FilamentProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentProfiles_Name_Material_SlicerType",
                table: "FilamentProfiles",
                columns: new[] { "Name", "Material", "SlicerType" },
                unique: true);
        }
    }
}
