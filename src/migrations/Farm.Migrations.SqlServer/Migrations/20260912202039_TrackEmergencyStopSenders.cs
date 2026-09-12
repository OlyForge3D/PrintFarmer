using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class TrackEmergencyStopSenders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE [PrinterControlOperations] SET [EmergencyStopInFlight] = 1
                WHERE [State] = 'Recovering' AND [FailureCode] = 'emergency_stop_outcome_unknown';
                """);

            migrationBuilder.CreateTable(
                name: "PrinterEmergencyStopAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ConfigurationIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Delivery = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SendCommittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterEmergencyStopAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterEmergencyStopAttempts_PrinterControlOperations_OperationId",
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
                UPDATE [PrinterControlOperations] SET [EmergencyStopInFlight] = 1
                WHERE [State] = 'Recovering' AND [Id] IN
                    (SELECT [OperationId] FROM [PrinterEmergencyStopAttempts] WHERE [Delivery] IN ('Pending', 'Unknown'));
                """);

            migrationBuilder.DropTable(
                name: "PrinterEmergencyStopAttempts");
        }
    }
}
