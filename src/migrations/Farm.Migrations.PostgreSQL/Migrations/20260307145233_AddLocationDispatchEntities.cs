using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
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
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DispatchScore",
                table: "PrintJobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DispatchedAt",
                table: "PrintJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Depth",
                table: "Locations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "Locations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Path",
                table: "Locations",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "/");

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Locations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalPrinterCount",
                table: "Locations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DispatchLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: true),
                    ScoreBreakdown = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AutoDispatchEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AutoDispatchMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdleThresholdSeconds = table.Column<int>(type: "integer", nullable: false),
                    MinimumScoreThreshold = table.Column<double>(type: "double precision", nullable: false),
                    MaxConcurrentDispatches = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                unique: true);

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
