using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RetireGcodeHarvest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The legacy G-code harvest permission is no longer seeded or enforced. Remove its
            // grants explicitly (rather than relying on the FK cascade), then the resource itself.
            // G-code library files, their harvest provenance and printed-part harvesting are kept.
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions"
                WHERE "ResourceId" IN (
                    SELECT "Id" FROM "Resources" WHERE "Name" = 'gcode_harvest');
                """);
            migrationBuilder.Sql("""
                DELETE FROM "Resources" WHERE "Name" = 'gcode_harvest';
                """);

            // Owner-approved permanent deletion (#3531): legacy harvest history is not archived.
            migrationBuilder.DropTable(
                name: "GcodeHarvestQueueItems");

            migrationBuilder.DropTable(
                name: "HarvestFileGcodeFileMappings");

            migrationBuilder.DropTable(
                name: "HarvestDiscoveredFiles");

            migrationBuilder.DropTable(
                name: "GcodeHarvestOperations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "G-code harvest retirement cannot be reversed: its operations, queue, discovered files, file mappings and gcode_harvest grants were deleted without an archive. Restore a pre-upgrade backup instead.");
        }
    }
}
