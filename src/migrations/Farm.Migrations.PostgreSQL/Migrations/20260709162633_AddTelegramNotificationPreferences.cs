using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableTelegramNotifications",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TelegramOnJobCompleted",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TelegramOnJobFailed",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TelegramOnJobPaused",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TelegramOnJobStarted",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableTelegramNotifications",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "TelegramOnJobCompleted",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "TelegramOnJobFailed",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "TelegramOnJobPaused",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "TelegramOnJobStarted",
                table: "NotificationPreferences");
        }
    }
}
