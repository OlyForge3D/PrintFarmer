using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterFileEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrinterFileThumbnailArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", unicode: false, maxLength: 32, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileThumbnailArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileThumbnailReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileThumbnailReservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileThumbnailStorageLocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileThumbnailStorageLocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrinterFileEnrichments",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    SourceIdentity = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    ExtractorVersion = table.Column<int>(type: "integer", nullable: false),
                    MetadataState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ThumbnailState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MetadataReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ThumbnailReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MetadataSource = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcquiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFileEnrichments", x => x.LocationId);
                    table.ForeignKey(
                        name: "FK_PrinterFileEnrichments_PrinterFileLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "PrinterFileLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrinterFileEnrichments_PrinterFileThumbnailArtifacts_Artifa~",
                        column: x => x.ArtifactId,
                        principalTable: "PrinterFileThumbnailArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "PrinterFileThumbnailStorageLocks",
                columns: new[] { "Id", "Revision" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileEnrichments_ArtifactId",
                table: "PrinterFileEnrichments",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileEnrichments_NextAttemptAtUtc",
                table: "PrinterFileEnrichments",
                column: "NextAttemptAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileThumbnailArtifacts_ContentSha256",
                table: "PrinterFileThumbnailArtifacts",
                column: "ContentSha256");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileThumbnailArtifacts_RetiredAtUtc",
                table: "PrinterFileThumbnailArtifacts",
                column: "RetiredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFileThumbnailReservations_ExpiresAtUtc",
                table: "PrinterFileThumbnailReservations",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrinterFileEnrichments");

            migrationBuilder.DropTable(
                name: "PrinterFileThumbnailReservations");

            migrationBuilder.DropTable(
                name: "PrinterFileThumbnailStorageLocks");

            migrationBuilder.DropTable(
                name: "PrinterFileThumbnailArtifacts");
        }
    }
}
