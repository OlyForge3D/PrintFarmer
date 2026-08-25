using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DropGeneratedProfileRevisionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GeneratedProfileRevisionOperations");

            migrationBuilder.DropTable(
                name: "GeneratedProfileRevisions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GeneratedProfileRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BedTemperature = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBySubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExactProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    FlowRatio = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    GenerationRequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GeneratorVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MaximumVolumetricFlow = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NormalizedSettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    NozzleTemperature = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentRevisionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PressureAdvance = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    PressureAdvanceSmoothTime = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    ProfileType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RetractionLength = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    RetractionLiftZ = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    RetractionMinimumTravel = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    RetractionSpeed = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    RevisionNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SlicerContainerDigest = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SlicerDistribution = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SlicerEngine = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SlicerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourceAttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceFilamentProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceMachineProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceProcessProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceProfileFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedProfileRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GeneratedProfileRevisions_CalibrationAttempts_SourceAttemptId",
                        column: x => x.SourceAttemptId,
                        principalTable: "CalibrationAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GeneratedProfileRevisions_CalibrationProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "CalibrationProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedProfileRevisionOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorSubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExportFormat = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    GeneratedProfileRevisionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OperationType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PublishedProfileId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedProfileRevisionOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GeneratedProfileRevisionOperations_GeneratedProfileRevisions_GeneratedProfileRevisionId",
                        column: x => x.GeneratedProfileRevisionId,
                        principalTable: "GeneratedProfileRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedProfileRevisionOperations_GeneratedProfileRevisionId_OperationId",
                table: "GeneratedProfileRevisionOperations",
                columns: new[] { "GeneratedProfileRevisionId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedProfileRevisions_ProjectId_GenerationRequestId",
                table: "GeneratedProfileRevisions",
                columns: new[] { "ProjectId", "GenerationRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedProfileRevisions_ProjectId_RevisionNumber",
                table: "GeneratedProfileRevisions",
                columns: new[] { "ProjectId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedProfileRevisions_SourceAttemptId",
                table: "GeneratedProfileRevisions",
                column: "SourceAttemptId");
        }
    }
}
