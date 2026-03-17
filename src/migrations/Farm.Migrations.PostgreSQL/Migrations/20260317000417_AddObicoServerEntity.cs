using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddObicoServerEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ObicoServerId",
                table: "Printers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ObicoServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxConcurrentAnalyses = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObicoServers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Printers_ObicoServerId",
                table: "Printers",
                column: "ObicoServerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_ObicoServers_ObicoServerId",
                table: "Printers",
                column: "ObicoServerId",
                principalTable: "ObicoServers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Printers_ObicoServers_ObicoServerId",
                table: "Printers");

            migrationBuilder.DropTable(
                name: "ObicoServers");

            migrationBuilder.DropIndex(
                name: "IX_Printers_ObicoServerId",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "ObicoServerId",
                table: "Printers");
        }
    }
}
