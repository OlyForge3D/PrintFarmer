using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class HicksV5NotificationPrefsAttentionDefaults : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobPaused",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "bit",
            oldDefaultValue: true);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobFailed",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "bit",
            oldDefaultValue: true);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobCompleted",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "bit",
            oldDefaultValue: true);

        migrationBuilder.Sql(
            """
            UPDATE [NotificationPreferences]
            SET [EmailOnJobStarted] = 0, [EmailOnJobCompleted] = 0,
                [EmailOnJobFailed] = 0, [EmailOnJobPaused] = 0,
                [EmailOnPrinterFailure] = 0, [EmailOnFilamentRunout] = 0,
                [EmailOnHarvestReady] = 0, [EmailOnMaintenanceDue] = 0,
                [EmailOnPrinterOffline] = 0
            WHERE [EnableEmailNotifications] = 0;
            """);
        migrationBuilder.Sql(
            """
            UPDATE [NotificationPreferences]
            SET [PushOnJobStarted] = 0, [PushOnJobCompleted] = 0,
                [PushOnJobFailed] = 0, [PushOnJobPaused] = 0,
                [PushOnPrinterFailure] = 0, [PushOnFilamentRunout] = 0,
                [PushOnHarvestReady] = 0, [PushOnMaintenanceDue] = 0,
                [PushOnPrinterOffline] = 0
            WHERE [EnablePushNotifications] = 0;
            """);
        migrationBuilder.Sql(
            """
            UPDATE [NotificationPreferences]
            SET [TelegramOnJobStarted] = 0, [TelegramOnJobCompleted] = 0,
                [TelegramOnJobFailed] = 0, [TelegramOnJobPaused] = 0,
                [TelegramOnPrinterFailure] = 0, [TelegramOnFilamentRunout] = 0,
                [TelegramOnHarvestReady] = 0, [TelegramOnMaintenanceDue] = 0,
                [TelegramOnPrinterOffline] = 0
            WHERE [EnableTelegramNotifications] = 0;
            """);
        migrationBuilder.Sql(
            """
            UPDATE [NotificationPreferences]
            SET [InAppOnJobStarted] = 0, [InAppOnJobCompleted] = 0,
                [InAppOnJobFailed] = 0, [InAppOnJobPaused] = 0,
                [InAppOnPrinterFailure] = 0, [InAppOnFilamentRunout] = 0,
                [InAppOnHarvestReady] = 0, [InAppOnMaintenanceDue] = 0,
                [InAppOnPrinterOffline] = 0
            WHERE [EnableInAppNotifications] = 0;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobPaused",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "bit",
            oldDefaultValue: false);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobFailed",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "bit",
            oldDefaultValue: false);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobCompleted",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "bit",
            oldDefaultValue: false);
    }
}
