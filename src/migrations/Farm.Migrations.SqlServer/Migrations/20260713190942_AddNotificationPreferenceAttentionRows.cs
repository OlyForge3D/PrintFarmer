using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddNotificationPreferenceAttentionRows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "EmailOnFilamentRunout",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnHarvestReady",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnMaintenanceDue",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnPrinterFailure",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "EmailOnPrinterOffline",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnFilamentRunout",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnHarvestReady",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnMaintenanceDue",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnPrinterFailure",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "InAppOnPrinterOffline",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnFilamentRunout",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnHarvestReady",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnMaintenanceDue",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnPrinterFailure",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "PushOnPrinterOffline",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnFilamentRunout",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnHarvestReady",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnMaintenanceDue",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnPrinterFailure",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TelegramOnPrinterOffline",
            table: "NotificationPreferences",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "EmailOnFilamentRunout",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnHarvestReady",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnMaintenanceDue",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnPrinterFailure",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "EmailOnPrinterOffline",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnFilamentRunout",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnHarvestReady",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnMaintenanceDue",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnPrinterFailure",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "InAppOnPrinterOffline",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnFilamentRunout",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnHarvestReady",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnMaintenanceDue",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnPrinterFailure",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "PushOnPrinterOffline",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnFilamentRunout",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnHarvestReady",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnMaintenanceDue",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnPrinterFailure",
            table: "NotificationPreferences");

        migrationBuilder.DropColumn(
            name: "TelegramOnPrinterOffline",
            table: "NotificationPreferences");
    }
}
