using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationDispatchEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Locations_Name",
                table: "Locations");

            migrationBuilder.AddColumn<int>(
                name: "DispatchMode",
                table: "PrintJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DispatchScore",
                table: "PrintJobs",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DispatchedAt",
                table: "PrintJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Depth",
                table: "Locations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "Locations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Path",
                table: "Locations",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "/");

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Locations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalPrinterCount",
                table: "Locations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DispatchLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Score = table.Column<double>(type: "float", nullable: true),
                    ScoreBreakdown = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DispatchLogs_PrintJobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DispatchLogs_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DispatchSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AutoDispatchEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AutoDispatchMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IdleThresholdSeconds = table.Column<int>(type: "int", nullable: false),
                    MinimumScoreThreshold = table.Column<double>(type: "float", nullable: false),
                    MaxConcurrentDispatches = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "DispatchSettings",
                columns: new[] { "Id", "AutoDispatchEnabled", "AutoDispatchMode", "IdleThresholdSeconds", "MaxConcurrentDispatches", "MinimumScoreThreshold", "UpdatedAt" },
                values: new object[] { 1, false, "Manual", 30, 3, 0.5, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ParentId",
                table: "Locations",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ParentId_Name",
                table: "Locations",
                columns: new[] { "ParentId", "Name" },
                unique: true,
                filter: "[ParentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Path",
                table: "Locations",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchLogs_CreatedAtUtc",
                table: "DispatchLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchLogs_PrinterId",
                table: "DispatchLogs",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchLogs_PrintJobId",
                table: "DispatchLogs",
                column: "PrintJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_Locations_Locations_ParentId",
                table: "Locations",
                column: "ParentId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Locations_Locations_ParentId",
                table: "Locations");

            migrationBuilder.DropTable(
                name: "DispatchLogs");

            migrationBuilder.DropTable(
                name: "DispatchSettings");

            migrationBuilder.DropIndex(
                name: "IX_Locations_ParentId",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Locations_ParentId_Name",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Locations_Path",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "DispatchMode",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "DispatchScore",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "Depth",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "Path",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "TotalPrinterCount",
                table: "Locations");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Name",
                table: "Locations",
                column: "Name",
                unique: true);
        }
    }
}
