using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    X = table.Column<double>(type: "float", nullable: true),
                    Y = table.Column<double>(type: "float", nullable: true),
                    Z = table.Column<double>(type: "float", nullable: true),
                    F = table.Column<double>(type: "float", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedIntent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PrinterConfigurationIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OwnerToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerHeartbeatAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SendCommittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletionEvidence = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    SenderIsolation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RecoveryRequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SenderIsolatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RecoveryActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RecoveryEvidenceJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: true),
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
