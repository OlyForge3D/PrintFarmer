using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCalibrationMethodProgressAndDraftProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalibrationDraftProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PromotedProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromotedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalibrationDraftProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalibrationDraftProfiles_CalibrationProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "CalibrationProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalibrationMethodProgresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Disposition = table.Column<int>(type: "int", nullable: false),
                    CurrentStepId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedBySubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalibrationMethodProgresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalibrationMethodProgresses_CalibrationProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "CalibrationProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalibrationDraftProfiles_ProjectId",
                table: "CalibrationDraftProfiles",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalibrationMethodProgresses_ProjectId_Method",
                table: "CalibrationMethodProgresses",
                columns: new[] { "ProjectId", "Method" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalibrationDraftProfiles");

            migrationBuilder.DropTable(
                name: "CalibrationMethodProgresses");
        }
    }
}
