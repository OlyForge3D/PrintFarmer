using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class EnforceNativePushInstallationIdentity : Migration
{
    private const string IndexName = "IX_DeviceTokens_UserId_InstallationId";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: IndexName,
            table: "DeviceTokens");

        migrationBuilder.AlterColumn<string>(
            name: "InstallationId",
            table: "DeviceTokens",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: false,
            collation: "Latin1_General_100_BIN2",
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128);

        migrationBuilder.CreateIndex(
            name: IndexName,
            table: "DeviceTokens",
            columns: new[] { "UserId", "InstallationId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A BIN2 deployment may contain case variants that the catalog collation merges.
        // Surface that rollback conflict while restoring the actual catalog collation.
        migrationBuilder.DropIndex(
            name: IndexName,
            table: "DeviceTokens");

        migrationBuilder.Sql(RevertCollationToCatalogDefault());

        migrationBuilder.CreateIndex(
            name: IndexName,
            table: "DeviceTokens",
            columns: new[] { "UserId", "InstallationId" },
            unique: true);
    }

    private static string RevertCollationToCatalogDefault()
    {
        return "DECLARE @coll_DeviceTokens_InstallationId sysname = "
            + "CAST(DATABASEPROPERTYEX(DB_NAME(), N'Collation') AS sysname);\n"
            + "DECLARE @sql_DeviceTokens_InstallationId nvarchar(max) = "
            + "N'ALTER TABLE [DeviceTokens] ALTER COLUMN [InstallationId] nvarchar(128) COLLATE ' "
            + "+ @coll_DeviceTokens_InstallationId + N' NOT NULL;';\n"
            + "EXEC sys.sp_executesql @sql_DeviceTokens_InstallationId;";
    }
}
