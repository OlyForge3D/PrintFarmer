using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddNozzleDiameterAndHasMmuToPrinter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasMmu",
                table: "Printers",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "NozzleDiameter",
                table: "Printers",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CameraSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CameraSnapshots_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CameraSnapshots_PrintJobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CameraSnapshots_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CameraSnapshots_CameraId",
                table: "CameraSnapshots",
                column: "CameraId");

            migrationBuilder.CreateIndex(
                name: "IX_CameraSnapshots_PrinterId",
                table: "CameraSnapshots",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_CameraSnapshots_PrintJobId",
                table: "CameraSnapshots",
                column: "PrintJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CameraSnapshots");

            migrationBuilder.DropColumn(
                name: "HasMmu",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "NozzleDiameter",
                table: "Printers");
        }
    }
}
