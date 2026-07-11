using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddAttentionSnoozes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttentionSnoozes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttentionItemId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SnoozedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttentionItemAnchorAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttentionSnoozes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttentionSnoozes_SnoozedUntilUtc",
                table: "AttentionSnoozes",
                column: "SnoozedUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AttentionSnoozes_UserId_AttentionItemId",
                table: "AttentionSnoozes",
                columns: new[] { "UserId", "AttentionItemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttentionSnoozes");
        }
    }
}
