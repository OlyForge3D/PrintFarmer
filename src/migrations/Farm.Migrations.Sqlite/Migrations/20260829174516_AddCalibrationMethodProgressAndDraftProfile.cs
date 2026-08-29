using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBySubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedBySubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PromotedProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PromotedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Disposition = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentStepId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBySubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedBySubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
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
