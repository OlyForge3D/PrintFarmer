using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddDurablePrinterControlOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrinterControlOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    X = table.Column<double>(type: "REAL", nullable: true),
                    Y = table.Column<double>(type: "REAL", nullable: true),
                    Z = table.Column<double>(type: "REAL", nullable: true),
                    F = table.Column<double>(type: "REAL", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 1L),
                    ActorSubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NormalizedIntent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PrinterConfigurationIdentity = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerToken = table.Column<Guid>(type: "TEXT", nullable: true),
                    OwnerHeartbeatAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SendCommittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletionEvidence = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FailureMessage = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SenderIsolation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecoveryRequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SenderIsolatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecoveryActorSubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RecoveryEvidenceJson = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    RecoveryFromRevision = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterControlOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterControlOperations_PrinterId_CreatedAtUtc",
                table: "PrinterControlOperations",
                columns: new[] { "PrinterId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterControlOperations_State_OwnerHeartbeatAtUtc",
                table: "PrinterControlOperations",
                columns: new[] { "State", "OwnerHeartbeatAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrinterControlOperations");
        }
    }
}
