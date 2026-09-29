using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ScopeMachineProcessNameIndexesPerOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessProfiles_Name_SlicerType_PrinterModelId",
                table: "ProcessProfiles");

            migrationBuilder.DropIndex(
                name: "IX_MachineProfiles_Name_SlicerType",
                table: "MachineProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_CreatedByUserId_Name_SlicerType_PrinterModelId",
                table: "ProcessProfiles",
                columns: new[] { "CreatedByUserId", "Name", "SlicerType", "PrinterModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_Name_SlicerType_PrinterModelId_Unowned",
                table: "ProcessProfiles",
                columns: new[] { "Name", "SlicerType", "PrinterModelId" },
                unique: true,
                filter: "\"CreatedByUserId\" IS NULL AND \"PrinterModelId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_CreatedByUserId_Name_SlicerType",
                table: "MachineProfiles",
                columns: new[] { "CreatedByUserId", "Name", "SlicerType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_Name_SlicerType_Unowned",
                table: "MachineProfiles",
                columns: new[] { "Name", "SlicerType" },
                unique: true,
                filter: "\"CreatedByUserId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessProfiles_CreatedByUserId_Name_SlicerType_PrinterModelId",
                table: "ProcessProfiles");

            migrationBuilder.DropIndex(
                name: "IX_ProcessProfiles_Name_SlicerType_PrinterModelId_Unowned",
                table: "ProcessProfiles");

            migrationBuilder.DropIndex(
                name: "IX_MachineProfiles_CreatedByUserId_Name_SlicerType",
                table: "MachineProfiles");

            migrationBuilder.DropIndex(
                name: "IX_MachineProfiles_Name_SlicerType_Unowned",
                table: "MachineProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessProfiles_Name_SlicerType_PrinterModelId",
                table: "ProcessProfiles",
                columns: new[] { "Name", "SlicerType", "PrinterModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineProfiles_Name_SlicerType",
                table: "MachineProfiles",
                columns: new[] { "Name", "SlicerType" },
                unique: true);
        }
    }
}
