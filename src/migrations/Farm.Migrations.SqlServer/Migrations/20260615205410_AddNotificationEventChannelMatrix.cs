using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
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
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnJobFailed",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnJobPaused",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnJobStarted",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InAppOnJobCompleted",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InAppOnJobFailed",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InAppOnJobPaused",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InAppOnJobStarted",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnJobCompleted",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnJobFailed",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnJobPaused",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushOnJobStarted",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE [NotificationPreferences]
                SET
                    [InAppOnJobStarted] = IIF([EnableInAppNotifications] = 1 AND [NotifyOnStart] = 1, 1, 0),
                    [InAppOnJobCompleted] = IIF([EnableInAppNotifications] = 1 AND [NotifyOnCompletion] = 1, 1, 0),
                    [InAppOnJobFailed] = 1,
                    [InAppOnJobPaused] = IIF([EnableInAppNotifications] = 1 AND [NotifyOnPause] = 1, 1, 0),
                    [EmailOnJobStarted] = IIF([EnableEmailNotifications] = 1 AND [NotifyOnStart] = 1, 1, 0),
                    [EmailOnJobCompleted] = IIF([EnableEmailNotifications] = 1 AND [NotifyOnCompletion] = 1, 1, 0),
                    [EmailOnJobFailed] = IIF([EnableEmailNotifications] = 1 AND [NotifyOnFailure] = 1, 1, 0),
                    [EmailOnJobPaused] = IIF([EnableEmailNotifications] = 1 AND [NotifyOnPause] = 1, 1, 0),
                    [PushOnJobStarted] = IIF([EnablePushNotifications] = 1 AND [NotifyOnStart] = 1, 1, 0),
                    [PushOnJobCompleted] = IIF([EnablePushNotifications] = 1 AND [NotifyOnCompletion] = 1, 1, 0),
                    [PushOnJobFailed] = IIF([EnablePushNotifications] = 1 AND [NotifyOnFailure] = 1, 1, 0),
                    [PushOnJobPaused] = IIF([EnablePushNotifications] = 1 AND [NotifyOnPause] = 1, 1, 0);
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
