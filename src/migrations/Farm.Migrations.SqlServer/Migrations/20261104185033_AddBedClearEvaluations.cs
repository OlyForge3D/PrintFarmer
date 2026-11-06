using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddBedClearEvaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BedClearEvaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BedTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceAcknowledgementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcknowledgedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ImageFilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CameraConfigurationAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsReferenceEligible = table.Column<bool>(type: "bit", nullable: false),
                    ProcessingStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ToolheadX = table.Column<double>(type: "float", nullable: true),
                    ToolheadY = table.Column<double>(type: "float", nullable: true),
                    ToolheadZ = table.Column<double>(type: "float", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Verdict = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Score = table.Column<double>(type: "float", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    GroundTruth = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    GroundTruthBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    GroundTruthAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActiveReference = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BedClearEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BedClearEvaluations_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BedClearEvaluations_PrintJobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BedClearEvaluations_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BedClearEvaluations_BedTypeId",
                table: "BedClearEvaluations",
                column: "BedTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_BedClearEvaluations_CameraId",
                table: "BedClearEvaluations",
                column: "CameraId");

            migrationBuilder.CreateIndex(
                name: "IX_BedClearEvaluations_PrintJobId",
                table: "BedClearEvaluations",
                column: "PrintJobId");

            migrationBuilder.CreateIndex(
                name: "IX_BedClearEvaluations_ReferenceScope",
                table: "BedClearEvaluations",
                columns: new[] { "PrinterId", "CameraId", "BedTypeId", "IsActiveReference", "GroundTruth" });

            migrationBuilder.CreateIndex(
                name: "IX_BedClearEvaluations_State",
                table: "BedClearEvaluations",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "UX_BedClearEvaluations_Acknowledgement",
                table: "BedClearEvaluations",
                column: "SourceAcknowledgementId",
                unique: true,
                filter: "[SourceAcknowledgementId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BedClearEvaluations");
        }
    }
}
