using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RemovePrinterCameraColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Migrate existing camera URLs from Printers to Cameras table before dropping columns.
            // Only inserts for printers that have camera URLs AND don't already have a Camera row.
            migrationBuilder.Sql("""
                INSERT INTO "Cameras" ("Id", "Name", "StreamUrl", "SnapshotUrl", "PrinterId", "Source", "CameraType", "HealthStatus", "IsEnabled", "SortOrder", "ConsecutiveFailures", "CreatedAt")
                SELECT
                    gen_random_uuid(),
                    'Migrated Camera',
                    p."CameraStreamUrl",
                    p."CameraSnapshotUrl",
                    p."Id",
                    'Standalone',
                    'General',
                    'Unknown',
                    true,
                    0,
                    0,
                    NOW()
                FROM "Printers" p
                WHERE (p."CameraStreamUrl" IS NOT NULL OR p."CameraSnapshotUrl" IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM "Cameras" c WHERE c."PrinterId" = p."Id")
                """);

            migrationBuilder.DropColumn(
                name: "CameraSnapshotUrl",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CameraStreamUrl",
                table: "Printers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CameraSnapshotUrl",
                table: "Printers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CameraStreamUrl",
                table: "Printers",
                type: "text",
                nullable: true);
        }
    }
}
