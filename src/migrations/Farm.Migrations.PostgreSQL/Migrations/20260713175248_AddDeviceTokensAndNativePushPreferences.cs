using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddDeviceTokensAndNativePushPreferences : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AttentionPushCategoryPreferencesJson",
            table: "NotificationPreferences",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "DeviceTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                InstallationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Token = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                AppBundleId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastFailureAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ConsecutiveFailureCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeviceTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_DeviceTokens_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_Token",
            table: "DeviceTokens",
            column: "Token");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_UserId_InstallationId",
            table: "DeviceTokens",
            columns: new[] { "UserId", "InstallationId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DeviceTokens");

        migrationBuilder.DropColumn(
            name: "AttentionPushCategoryPreferencesJson",
            table: "NotificationPreferences");
    }
}
