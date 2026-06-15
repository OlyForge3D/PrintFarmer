using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationEventChannelMatrix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailOnJobCompleted",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnJobFailed",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnJobPaused",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnJobStarted",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InAppOnJobCompleted",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InAppOnJobFailed",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InAppOnJobPaused",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InAppOnJobStarted",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnJobCompleted",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnJobFailed",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnJobPaused",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnJobStarted",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "NotificationPreferences"
                SET
                    "InAppOnJobStarted" = "EnableInAppNotifications" AND "NotifyOnStart",
                    "InAppOnJobCompleted" = "EnableInAppNotifications" AND "NotifyOnCompletion",
                    "InAppOnJobFailed" = TRUE,
                    "InAppOnJobPaused" = "EnableInAppNotifications" AND "NotifyOnPause",
                    "EmailOnJobStarted" = "EnableEmailNotifications" AND "NotifyOnStart",
                    "EmailOnJobCompleted" = "EnableEmailNotifications" AND "NotifyOnCompletion",
                    "EmailOnJobFailed" = "EnableEmailNotifications" AND "NotifyOnFailure",
                    "EmailOnJobPaused" = "EnableEmailNotifications" AND "NotifyOnPause",
                    "PushOnJobStarted" = "EnablePushNotifications" AND "NotifyOnStart",
                    "PushOnJobCompleted" = "EnablePushNotifications" AND "NotifyOnCompletion",
                    "PushOnJobFailed" = "EnablePushNotifications" AND "NotifyOnFailure",
                    "PushOnJobPaused" = "EnablePushNotifications" AND "NotifyOnPause";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailOnJobCompleted",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailOnJobFailed",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailOnJobPaused",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailOnJobStarted",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "InAppOnJobCompleted",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "InAppOnJobFailed",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "InAppOnJobPaused",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "InAppOnJobStarted",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "PushOnJobCompleted",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "PushOnJobFailed",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "PushOnJobPaused",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "PushOnJobStarted",
                table: "NotificationPreferences");
        }
    }
}
