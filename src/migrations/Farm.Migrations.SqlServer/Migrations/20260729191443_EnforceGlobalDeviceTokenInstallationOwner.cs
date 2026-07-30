using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class EnforceGlobalDeviceTokenInstallationOwner : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            WITH [RankedOwners] AS (
                SELECT
                    [Id],
                    ROW_NUMBER() OVER (
                        PARTITION BY [InstallationId]
                        ORDER BY
                            [RegistrationVersion] DESC,
                            COALESCE([LastUsedAt], [CreatedAt]) DESC,
                            [CreatedAt] DESC,
                            [Id] DESC
                    ) AS [OwnerRank]
                FROM [DeviceTokens]
                WHERE [IsActive] = 1
            )
            UPDATE [Target]
            SET [IsActive] = 0
            FROM [DeviceTokens] AS [Target]
            INNER JOIN [RankedOwners]
                ON [Target].[Id] = [RankedOwners].[Id]
            WHERE [RankedOwners].[OwnerRank] > 1;
            """);

        migrationBuilder.Sql(
            """
            IF EXISTS (
                SELECT 1
                FROM [sys].[indexes]
                WHERE [name] = N'IX_DeviceTokens_UserId_InstallationId'
                  AND [object_id] = OBJECT_ID(N'[DeviceTokens]')
            )
                DROP INDEX [IX_DeviceTokens_UserId_InstallationId] ON [DeviceTokens];
            IF EXISTS (
                SELECT 1
                FROM [sys].[indexes]
                WHERE [name] = N'IX_DeviceTokens_InstallationId'
                  AND [object_id] = OBJECT_ID(N'[DeviceTokens]')
            )
                DROP INDEX [IX_DeviceTokens_InstallationId] ON [DeviceTokens];
            """);

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_InstallationId",
            table: "DeviceTokens",
            column: "InstallationId",
            unique: true,
            filter: "[IsActive] = 1");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_UserId",
            table: "DeviceTokens",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_DeviceTokens_InstallationId",
            table: "DeviceTokens");

        migrationBuilder.DropIndex(
            name: "IX_DeviceTokens_UserId",
            table: "DeviceTokens");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_UserId_InstallationId",
            table: "DeviceTokens",
            columns: new[] { "UserId", "InstallationId" },
            unique: true,
            filter: "[IsActive] = 1");
    }
}
