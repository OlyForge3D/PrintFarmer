using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    X = table.Column<double>(type: "double precision", nullable: true),
                    Y = table.Column<double>(type: "double precision", nullable: true),
                    Z = table.Column<double>(type: "double precision", nullable: true),
                    F = table.Column<double>(type: "double precision", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ActorSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedIntent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PrinterConfigurationIdentity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerToken = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerHeartbeatAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SendCommittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletionEvidence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SenderIsolation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecoveryRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SenderIsolatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecoveryActorSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RecoveryEvidenceJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    RecoveryFromRevision = table.Column<long>(type: "bigint", nullable: true)
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
