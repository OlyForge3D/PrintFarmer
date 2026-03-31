using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddFailureDetectionIncidentHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FailureDetectionIncidents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                JobName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                Confidence = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                SnapshotUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                AutoPaused = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FailureDetectionIncidents", x => x.Id);
                table.ForeignKey(
                    name: "FK_FailureDetectionIncidents_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FailureDetectionIncidents_DetectedAt",
            table: "FailureDetectionIncidents",
            column: "DetectedAt");

        migrationBuilder.CreateIndex(
            name: "IX_FailureDetectionIncidents_PrinterId_DetectedAt",
            table: "FailureDetectionIncidents",
            columns: new[] { "PrinterId", "DetectedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "FailureDetectionIncidents");
    }
}
