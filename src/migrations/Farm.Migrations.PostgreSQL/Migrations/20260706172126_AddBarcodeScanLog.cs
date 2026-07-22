using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Barcode = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: false),
                    MatchedFilamentId = table.Column<int>(type: "integer", nullable: true),
                    CreatedSpoolId = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
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
