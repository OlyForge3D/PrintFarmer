using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchOperatorRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BackendSenderSettledAtUtc",
                table: "QueueDispatchAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DispatchEscalationMarkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DispatchAttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PolicyRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    Threshold = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClaimAgeSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchEscalationMarkers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DispatchRecoveryJournalEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrinterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DispatchAttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PriorOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Transition = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ActorSubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ActorRecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ServerRecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClientReportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AssertionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PhysicalCheckConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SenderIsolationConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SenderSettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ReplayScopeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseETag = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResponseBodyJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchRecoveryJournalEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_DispatchEscalationMarkers_Attempt_Policy_Threshold",
                table: "DispatchEscalationMarkers",
                columns: new[] { "DispatchAttemptId", "PolicyRevision", "Threshold" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DispatchRecoveryJournal_Attempt",
                table: "DispatchRecoveryJournalEntries",
                column: "DispatchAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchRecoveryJournal_Printer_Recorded",
                table: "DispatchRecoveryJournalEntries",
                columns: new[] { "PrinterId", "ServerRecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_DispatchRecoveryJournal_ReplayScope",
                table: "DispatchRecoveryJournalEntries",
                column: "ReplayScopeHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DispatchEscalationMarkers");

            migrationBuilder.DropTable(
                name: "DispatchRecoveryJournalEntries");

            migrationBuilder.DropColumn(
                name: "BackendSenderSettledAtUtc",
                table: "QueueDispatchAttempts");
        }
    }
}
