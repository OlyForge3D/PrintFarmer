using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class ExtendCaseSensitiveCollationToSkuAndOperationKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // OperationKey / HarvestOperationKey have no dependent CHECK constraints,
        // so EF's AlterColumn (which auto-drops/recreates the filtered unique index)
        // is sufficient. SQL Server rewrites these to DROP INDEX -> ALTER COLUMN COLLATE
        // -> CREATE INDEX at SQL-generation time.
        migrationBuilder.AlterColumn<string>(
            name: "HarvestOperationKey",
            table: "PrintJobs",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true,
            collation: "Latin1_General_100_BIN2",
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "OperationKey",
            table: "PartInventoryAdjustments",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true,
            collation: "Latin1_General_100_BIN2",
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128,
            oldNullable: true);

        // Sku and Code each have a CHECK constraint (CK_..._Normalized) that references
        // the column. SQL Server forbids ALTER COLUMN ... COLLATE while a CHECK constraint
        // depends on the column (Msg 5074 / 4922), and EF does not auto-manage user-defined
        // CHECK constraints during AlterColumn. Drop the constraint, alter the collation
        // (EF still auto-handles the dependent unique index), then recreate the constraint
        // with its original definition so the model snapshot stays consistent.
        migrationBuilder.Sql("ALTER TABLE [PartInventories] DROP CONSTRAINT IF EXISTS [CK_PartInventories_Sku_Normalized];");

        migrationBuilder.AlterColumn<string>(
            name: "Sku",
            table: "PartInventories",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: false,
            collation: "Latin1_General_100_BIN2",
            oldClrType: typeof(string),
            oldType: "nvarchar(64)",
            oldMaxLength: 64);

        migrationBuilder.Sql("ALTER TABLE [PartInventories] ADD CONSTRAINT [CK_PartInventories_Sku_Normalized] CHECK ([Sku] = UPPER([Sku]));");

        migrationBuilder.Sql("ALTER TABLE [Bins] DROP CONSTRAINT IF EXISTS [CK_Bins_Code_Normalized];");

        migrationBuilder.AlterColumn<string>(
            name: "Code",
            table: "Bins",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: false,
            collation: "Latin1_General_100_BIN2",
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128);

        migrationBuilder.Sql("ALTER TABLE [Bins] ADD CONSTRAINT [CK_Bins_Code_Normalized] CHECK ([Code] = UPPER([Code]));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "HarvestOperationKey",
            table: "PrintJobs",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128,
            oldNullable: true,
            oldCollation: "Latin1_General_100_BIN2");

        migrationBuilder.AlterColumn<string>(
            name: "OperationKey",
            table: "PartInventoryAdjustments",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128,
            oldNullable: true,
            oldCollation: "Latin1_General_100_BIN2");

        // Mirror the CHECK-constraint drop/recreate around the collation revert for Sku/Code.
        migrationBuilder.Sql("ALTER TABLE [PartInventories] DROP CONSTRAINT IF EXISTS [CK_PartInventories_Sku_Normalized];");

        migrationBuilder.AlterColumn<string>(
            name: "Sku",
            table: "PartInventories",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(64)",
            oldMaxLength: 64,
            oldCollation: "Latin1_General_100_BIN2");

        migrationBuilder.Sql("ALTER TABLE [PartInventories] ADD CONSTRAINT [CK_PartInventories_Sku_Normalized] CHECK ([Sku] = UPPER([Sku]));");

        migrationBuilder.Sql("ALTER TABLE [Bins] DROP CONSTRAINT IF EXISTS [CK_Bins_Code_Normalized];");

        migrationBuilder.AlterColumn<string>(
            name: "Code",
            table: "Bins",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(128)",
            oldMaxLength: 128,
            oldCollation: "Latin1_General_100_BIN2");

        migrationBuilder.Sql("ALTER TABLE [Bins] ADD CONSTRAINT [CK_Bins_Code_Normalized] CHECK ([Code] = UPPER([Code]));");
    }
}
