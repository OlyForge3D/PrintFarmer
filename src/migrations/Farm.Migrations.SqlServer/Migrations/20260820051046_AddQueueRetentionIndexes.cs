using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddQueueRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_QueueDispatchOutbox_Status_CompletedAt",
                table: "QueueDispatchOutbox",
                columns: new[] { "Status", "CompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueDispatchAttempts_RequiresReconciliation_TerminalAt",
                table: "QueueDispatchAttempts",
                columns: new[] { "RequiresReconciliation", "TerminalAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueueDispatchOutbox_Status_CompletedAt",
                table: "QueueDispatchOutbox");

            migrationBuilder.DropIndex(
                name: "IX_QueueDispatchAttempts_RequiresReconciliation_TerminalAt",
                table: "QueueDispatchAttempts");
        }
    }
}
