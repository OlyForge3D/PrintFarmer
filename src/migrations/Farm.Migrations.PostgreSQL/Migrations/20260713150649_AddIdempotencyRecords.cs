using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddIdempotencyRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IdempotencyRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                RouteKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ResponseStatusCode = table.Column<int>(type: "integer", nullable: true),
                ResponseContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ResponseBody = table.Column<byte[]>(type: "bytea", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_IdempotencyRecords_CreatedAt",
            table: "IdempotencyRecords",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_IdempotencyRecords_User_Route_Key",
            table: "IdempotencyRecords",
            columns: new[] { "UserId", "RouteKey", "IdempotencyKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "IdempotencyRecords");
    }
}
