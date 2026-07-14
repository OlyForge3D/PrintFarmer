using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class HicksV5NotificationPrefsAttentionDefaults : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobPaused",
            table: "NotificationPreferences",
            type: "boolean",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: true);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobFailed",
            table: "NotificationPreferences",
            type: "boolean",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: true);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobCompleted",
            table: "NotificationPreferences",
            type: "boolean",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: true);

        migrationBuilder.Sql(
            """
            UPDATE "NotificationPreferences"
            SET "EmailOnJobStarted" = FALSE, "EmailOnJobCompleted" = FALSE,
                "EmailOnJobFailed" = FALSE, "EmailOnJobPaused" = FALSE,
                "EmailOnPrinterFailure" = FALSE, "EmailOnFilamentRunout" = FALSE,
                "EmailOnHarvestReady" = FALSE, "EmailOnMaintenanceDue" = FALSE,
                "EmailOnPrinterOffline" = FALSE
            WHERE "EnableEmailNotifications" = FALSE;
            """);
        migrationBuilder.Sql(
            """
            UPDATE "NotificationPreferences"
            SET "PushOnJobStarted" = FALSE, "PushOnJobCompleted" = FALSE,
                "PushOnJobFailed" = FALSE, "PushOnJobPaused" = FALSE,
                "PushOnPrinterFailure" = FALSE, "PushOnFilamentRunout" = FALSE,
                "PushOnHarvestReady" = FALSE, "PushOnMaintenanceDue" = FALSE,
                "PushOnPrinterOffline" = FALSE
            WHERE "EnablePushNotifications" = FALSE;
            """);
        migrationBuilder.Sql(
            """
            UPDATE "NotificationPreferences"
            SET "TelegramOnJobStarted" = FALSE, "TelegramOnJobCompleted" = FALSE,
                "TelegramOnJobFailed" = FALSE, "TelegramOnJobPaused" = FALSE,
                "TelegramOnPrinterFailure" = FALSE, "TelegramOnFilamentRunout" = FALSE,
                "TelegramOnHarvestReady" = FALSE, "TelegramOnMaintenanceDue" = FALSE,
                "TelegramOnPrinterOffline" = FALSE
            WHERE "EnableTelegramNotifications" = FALSE;
            """);
        migrationBuilder.Sql(
            """
            UPDATE "NotificationPreferences"
            SET "InAppOnJobStarted" = FALSE, "InAppOnJobCompleted" = FALSE,
                "InAppOnJobFailed" = FALSE, "InAppOnJobPaused" = FALSE,
                "InAppOnPrinterFailure" = FALSE, "InAppOnFilamentRunout" = FALSE,
                "InAppOnHarvestReady" = FALSE, "InAppOnMaintenanceDue" = FALSE,
                "InAppOnPrinterOffline" = FALSE
            WHERE "EnableInAppNotifications" = FALSE;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobPaused",
            table: "NotificationPreferences",
            type: "boolean",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: false);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobFailed",
            table: "NotificationPreferences",
            type: "boolean",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: false);

        migrationBuilder.AlterColumn<bool>(
            name: "EmailOnJobCompleted",
            table: "NotificationPreferences",
            type: "boolean",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: false);
    }
}
