using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class FilterSliceJobIdempotencyToCalibration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_Owner_Project_Checksum",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.DropIndex(
            name: "IX_SliceJobs_Owner_Project_Correlation",
            schema: "slicer",
            table: "SliceJobs");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Owner_Project_Checksum",
            schema: "slicer",
            table: "SliceJobs",
            columns: new[] { "UserId", "IdempotencyScopeId", "Checksum" },
            unique: true,
            filter: "[Checksum] IS NOT NULL AND [IdempotencyScopeId] <> '00000000-0000-0000-0000-000000000000'");

        migrationBuilder.CreateIndex(
            name: "IX_SliceJobs_Owner_Project_Correlation",
            schema: "slicer",
            table: "SliceJobs",
            columns: new[] { "UserId", "IdempotencyScopeId", "CorrelationId" },
            unique: true,
            filter: "[CorrelationId] IS NOT NULL AND [IdempotencyScopeId] <> '00000000-0000-0000-0000-000000000000'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "Calibration-scoped slicer idempotency is forward-only because valid head " +
            "data can contain repeated standard-job checksums and correlations.");
    }
}
