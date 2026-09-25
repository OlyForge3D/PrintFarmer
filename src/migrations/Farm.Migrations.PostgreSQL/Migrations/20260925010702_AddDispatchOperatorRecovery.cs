using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
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
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DispatchEscalationMarkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DispatchAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    PolicyRevision = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClaimAgeSeconds = table.Column<long>(type: "bigint", nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchEscalationMarkers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DispatchRecoveryJournalEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    DispatchAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimRevision = table.Column<long>(type: "bigint", nullable: false),
                    PriorOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Transition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActorRecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ServerRecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientReportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssertionVersion = table.Column<int>(type: "integer", nullable: false),
                    PhysicalCheckConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    SenderIsolationConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    SenderSettledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EvidenceJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReplayScopeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "integer", nullable: false),
                    ResponseETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResponseBodyJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false)
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
