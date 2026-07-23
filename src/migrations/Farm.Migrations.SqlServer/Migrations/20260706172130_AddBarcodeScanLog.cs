using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddBarcodeScanLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BarcodeScanLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: false),
                    MatchedFilamentId = table.Column<int>(type: "int", nullable: true),
                    CreatedSpoolId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarcodeScanLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BarcodeScanLogs_Action",
                table: "BarcodeScanLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_BarcodeScanLogs_Barcode",
                table: "BarcodeScanLogs",
                column: "Barcode");

            migrationBuilder.CreateIndex(
                name: "IX_BarcodeScanLogs_Outcome",
                table: "BarcodeScanLogs",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_BarcodeScanLogs_Timestamp",
                table: "BarcodeScanLogs",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BarcodeScanLogs");
        }
    }
}
