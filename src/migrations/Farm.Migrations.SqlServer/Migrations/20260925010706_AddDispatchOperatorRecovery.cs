using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
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
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DispatchEscalationMarkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispatchAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PolicyRevision = table.Column<int>(type: "int", nullable: false),
                    Threshold = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClaimAgeSeconds = table.Column<long>(type: "bigint", nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchEscalationMarkers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DispatchRecoveryJournalEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DispatchAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimRevision = table.Column<long>(type: "bigint", nullable: false),
                    PriorOutcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Transition = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ActorRecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ServerRecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClientReportedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AssertionVersion = table.Column<int>(type: "int", nullable: false),
                    PhysicalCheckConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    SenderIsolationConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    SenderSettledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EvidenceJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ReplayScopeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "int", nullable: false),
                    ResponseETag = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ResponseBodyJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false)
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
