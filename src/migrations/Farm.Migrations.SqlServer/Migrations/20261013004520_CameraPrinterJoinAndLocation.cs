using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <summary>
    /// Replaces the single <c>Cameras.PrinterId</c> FK with the many-to-many <c>CameraPrinters</c> join,
    /// and adds <c>Cameras.LocationId</c> / <c>Cameras.UserModified</c>. Existing printer links are
    /// copied into the join (first camera per printer by SortOrder/Id becomes primary) and the legacy
    /// free-text <c>Cameras.Location</c> is mapped to <c>LocationId</c> by case-insensitive name match
    /// before <c>PrinterId</c> is dropped.
    /// </summary>
    public partial class CameraPrinterJoinAndLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Cameras",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UserModified",
                table: "Cameras",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CameraPrinters",
                columns: table => new
                {
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraPrinters", x => new { x.CameraId, x.PrinterId });
                    table.ForeignKey(
                        name: "FK_CameraPrinters_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CameraPrinters_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CameraPrinters_PrinterId",
                table: "CameraPrinters",
                column: "PrinterId");

            // Data: one join row per camera that had a PrinterId; the first camera per printer is primary.
            migrationBuilder.Sql(
                """
                INSERT INTO [CameraPrinters] ([CameraId], [PrinterId], [IsPrimary], [SortOrder])
                SELECT c.[Id], c.[PrinterId],
                       CASE WHEN ROW_NUMBER() OVER (PARTITION BY c.[PrinterId] ORDER BY c.[SortOrder], c.[Id]) = 1
                            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,
                       c.[SortOrder]
                FROM [Cameras] AS c
                WHERE c.[PrinterId] IS NOT NULL;
                """);

            // Data: map legacy free-text Location to a Location row by trimmed, case-insensitive name.
            migrationBuilder.Sql(
                """
                UPDATE c
                SET c.[LocationId] = (
                    SELECT TOP (1) l.[Id]
                    FROM [Locations] AS l
                    WHERE LOWER(LTRIM(RTRIM(l.[Name]))) = LOWER(LTRIM(RTRIM(c.[Location])))
                    ORDER BY l.[Depth], l.[Id])
                FROM [Cameras] AS c
                WHERE c.[Location] IS NOT NULL AND LTRIM(RTRIM(c.[Location])) <> N'';
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Cameras_Printers_PrinterId",
                table: "Cameras");

            migrationBuilder.DropIndex(
                name: "IX_Cameras_PrinterId",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "PrinterId",
                table: "Cameras");

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_LocationId",
                table: "Cameras",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Cameras_Locations_LocationId",
                table: "Cameras",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cameras_Locations_LocationId",
                table: "Cameras");

            migrationBuilder.DropIndex(
                name: "IX_Cameras_LocationId",
                table: "Cameras");

            migrationBuilder.AddColumn<Guid>(
                name: "PrinterId",
                table: "Cameras",
                type: "uniqueidentifier",
                nullable: true);

            // Data: restore the single printer link (primary first). Additional links are lost.
            migrationBuilder.Sql(
                """
                UPDATE c
                SET c.[PrinterId] = (
                    SELECT TOP (1) cp.[PrinterId]
                    FROM [CameraPrinters] AS cp
                    WHERE cp.[CameraId] = c.[Id]
                    ORDER BY cp.[IsPrimary] DESC, cp.[SortOrder], cp.[PrinterId])
                FROM [Cameras] AS c;
                """);

            migrationBuilder.DropTable(
                name: "CameraPrinters");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "UserModified",
                table: "Cameras");

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_PrinterId",
                table: "Cameras",
                column: "PrinterId");

            migrationBuilder.AddForeignKey(
                name: "FK_Cameras_Printers_PrinterId",
                table: "Cameras",
                column: "PrinterId",
                principalTable: "Printers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
