using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrintProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintProjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrintProjectFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    PrintProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    GcodeFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ColorRequirement = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    MaterialRequirement = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PrintCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    PrintedCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastPrintedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPrintJobId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintProjectFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintProjectFiles_GcodeFiles_GcodeFileId",
                        column: x => x.GcodeFileId,
                        principalTable: "GcodeFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrintProjectFiles_PrintJobs_LastPrintJobId",
                        column: x => x.LastPrintJobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PrintProjectFiles_PrintProjects_PrintProjectId",
                        column: x => x.PrintProjectId,
                        principalTable: "PrintProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectFiles_ColorRequirement",
                table: "PrintProjectFiles",
                column: "ColorRequirement");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectFiles_GcodeFileId",
                table: "PrintProjectFiles",
                column: "GcodeFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectFiles_LastPrintJobId",
                table: "PrintProjectFiles",
                column: "LastPrintJobId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectFiles_PrintProjectId",
                table: "PrintProjectFiles",
                column: "PrintProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectFiles_ProjectId_GcodeFileId",
                table: "PrintProjectFiles",
                columns: new[] { "PrintProjectId", "GcodeFileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectFiles_Status",
                table: "PrintProjectFiles",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjects_CreatedAt",
                table: "PrintProjects",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjects_DueDate",
                table: "PrintProjects",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjects_Priority",
                table: "PrintProjects",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjects_Status",
                table: "PrintProjects",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintProjectFiles");

            migrationBuilder.DropTable(
                name: "PrintProjects");
        }
    }
}
