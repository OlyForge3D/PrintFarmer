using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddNfcTagBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NfcTagBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TagUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SpoolId = table.Column<int>(type: "integer", nullable: true),
                    SpoolName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrayId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SpoolLastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NfcTagBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NfcTagBindings_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NfcTagBindings_PrinterId",
                table: "NfcTagBindings",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_NfcTagBindings_SpoolId",
                table: "NfcTagBindings",
                column: "SpoolId");

            migrationBuilder.CreateIndex(
                name: "IX_NfcTagBindings_TagUid",
                table: "NfcTagBindings",
                column: "TagUid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NfcTagBindings");
        }
    }
}
