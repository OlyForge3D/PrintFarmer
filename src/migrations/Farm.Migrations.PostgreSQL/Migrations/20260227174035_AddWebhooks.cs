using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddWebhooks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WebhookSubscriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                Secret = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                EventTypes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                MaxConsecutiveFailures = table.Column<int>(type: "integer", nullable: false, defaultValue: 10),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastDeliveryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebhookSubscriptions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WebhookDeliveryLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                WebhookSubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false),
                StatusCode = table.Column<int>(type: "integer", nullable: true),
                Success = table.Column<bool>(type: "boolean", nullable: false),
                ErrorMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                Attempt = table.Column<int>(type: "integer", nullable: false),
                DurationMs = table.Column<long>(type: "bigint", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebhookDeliveryLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_WebhookDeliveryLogs_WebhookSubscriptions_WebhookSubscriptio~",
                    column: x => x.WebhookSubscriptionId,
                    principalTable: "WebhookSubscriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryLogs_CreatedAt",
            table: "WebhookDeliveryLogs",
            column: "CreatedAt",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryLogs_EventType",
            table: "WebhookDeliveryLogs",
            column: "EventType");

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryLogs_Success",
            table: "WebhookDeliveryLogs",
            column: "Success");

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryLogs_WebhookSubscriptionId",
            table: "WebhookDeliveryLogs",
            column: "WebhookSubscriptionId");

        migrationBuilder.CreateIndex(
            name: "IX_WebhookSubscriptions_CreatedAt",
            table: "WebhookSubscriptions",
            column: "CreatedAt",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_WebhookSubscriptions_IsActive",
            table: "WebhookSubscriptions",
            column: "IsActive");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WebhookDeliveryLogs");

        migrationBuilder.DropTable(
            name: "WebhookSubscriptions");
    }
}
