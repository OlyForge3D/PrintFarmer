using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
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
                )
                DELETE FROM [RankedOwners]
                WHERE [OwnerRank] > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_DeviceTokens_UserId_InstallationId",
                table: "DeviceTokens");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_InstallationId",
                table: "DeviceTokens",
                column: "InstallationId",
                unique: true);

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
                unique: true);
        }
    }
}
