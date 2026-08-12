using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class HardenBedClearReplayStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_Idempotency_Calibration",
                table: "PrintJobs");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyScope",
                table: "PrintJobs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                table: "PrintJobs",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_Idempotency_Calibration",
                table: "PrintJobs",
                columns: new[] { "IdempotencyScope", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyScope] IS NOT NULL AND [IdempotencyKey] IS NOT NULL AND [JobKind] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_BedClearCommandRecords_Job_Created_Id",
                table: "BedClearCommandRecords",
                columns: new[] { "JobId", "CreatedAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "UX_BedClearCommandRecords_OutboxEventId",
                table: "BedClearCommandRecords",
                column: "OutboxEventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BedClearCommandRecords_Job_Created_Id",
                table: "BedClearCommandRecords");

            migrationBuilder.DropIndex(
                name: "UX_BedClearCommandRecords_OutboxEventId",
                table: "BedClearCommandRecords");

            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_Idempotency_Calibration",
                table: "PrintJobs");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyScope",
                table: "PrintJobs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                table: "PrintJobs",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_Idempotency_Calibration",
                table: "PrintJobs",
                columns: new[] { "IdempotencyScope", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyScope] IS NOT NULL AND [IdempotencyKey] IS NOT NULL AND [JobKind] = 1");
        }
    }
}
