using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class TrackEmergencyStopSenders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "PrinterControlOperations" SET "EmergencyStopInFlight" = TRUE
                WHERE "State" = 'Recovering' AND "FailureCode" = 'emergency_stop_outcome_unknown';
                """);

            migrationBuilder.CreateTable(
                name: "PrinterEmergencyStopAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConfigurationIdentity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Delivery = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SendCommittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterEmergencyStopAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterEmergencyStopAttempts_PrinterControlOperations_Opera~",
                        column: x => x.OperationId,
                        principalTable: "PrinterControlOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterEmergencyStopAttempts_OperationId_Delivery",
                table: "PrinterEmergencyStopAttempts",
                columns: new[] { "OperationId", "Delivery" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "PrinterControlOperations" SET "EmergencyStopInFlight" = TRUE
                WHERE "State" = 'Recovering' AND "Id" IN
                    (SELECT "OperationId" FROM "PrinterEmergencyStopAttempts" WHERE "Delivery" IN ('Pending', 'Unknown'));
                """);

            migrationBuilder.DropTable(
                name: "PrinterEmergencyStopAttempts");
        }
    }
}
