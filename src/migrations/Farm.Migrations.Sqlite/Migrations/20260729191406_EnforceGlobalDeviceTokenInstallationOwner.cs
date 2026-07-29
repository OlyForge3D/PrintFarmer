using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class EnforceGlobalDeviceTokenInstallationOwner : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            WITH "RankedOwners" AS (
                SELECT
                    "Id",
                    ROW_NUMBER() OVER (
                        PARTITION BY "InstallationId"
                        ORDER BY
                            "RegistrationVersion" DESC,
                            COALESCE("LastUsedAt", "CreatedAt") DESC,
                            "CreatedAt" DESC,
                            "Id" DESC
                    ) AS "OwnerRank"
                FROM "DeviceTokens"
                WHERE "IsActive" = 1
            )
            UPDATE "DeviceTokens"
            SET "IsActive" = 0
            WHERE "Id" IN (
                SELECT "Id"
                FROM "RankedOwners"
                WHERE "OwnerRank" > 1
            );
            """);

        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS "IX_DeviceTokens_UserId_InstallationId";
            DROP INDEX IF EXISTS "IX_DeviceTokens_InstallationId";
            """);

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_InstallationId",
            table: "DeviceTokens",
            column: "InstallationId",
            unique: true,
            filter: "\"IsActive\" = 1");

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
            filter: "\"IsActive\" = 1");
    }
}
