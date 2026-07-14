using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddPartOutputSnapshots : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "PartInventories");

        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "Bins");

        migrationBuilder.CreateTable(
            name: "PartHarvestOutputSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                PartInventoryAdjustmentId = table.Column<Guid>(type: "uuid", nullable: false),
                JobOutputSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                ExpectedBinId = table.Column<Guid>(type: "uuid", nullable: true),
                ExpectedBinCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ActualBinId = table.Column<Guid>(type: "uuid", nullable: false),
                ActualBinCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SourceFileId = table.Column<Guid>(type: "uuid", nullable: true),
                SourceMappingId = table.Column<Guid>(type: "uuid", nullable: true),
                OverrideApplied = table.Column<bool>(type: "boolean", nullable: false),
                OverrideReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PartHarvestOutputSnapshots", x => x.Id);
                table.CheckConstraint("CK_PartHarvestOutputSnapshots_ExpectedBin_Consistent", "(\"ExpectedBinId\" IS NULL AND \"ExpectedBinCode\" IS NULL) OR (\"ExpectedBinId\" IS NOT NULL AND \"ExpectedBinCode\" IS NOT NULL)");
                table.CheckConstraint("CK_PartHarvestOutputSnapshots_Quantity_Positive", "\"Quantity\" > 0");
                table.CheckConstraint("CK_PartHarvestOutputSnapshots_Sequence_NonNegative", "\"Sequence\" >= 0");
                table.CheckConstraint("CK_PartHarvestOutputSnapshots_Sku_Normalized", "\"Sku\" = UPPER(\"Sku\")");
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_Bins_ActualBinId",
                    column: x => x.ActualBinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_Bins_ExpectedBinId",
                    column: x => x.ExpectedBinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_PartInventories_PartInventoryId",
                    column: x => x.PartInventoryId,
                    principalTable: "PartInventories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_PartInventoryAdjustments_PartInv~",
                    column: x => x.PartInventoryAdjustmentId,
                    principalTable: "PartInventoryAdjustments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartHarvestOutputSnapshots_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrintJobPartOutputSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                PartInventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                QuantityPerPrint = table.Column<int>(type: "integer", nullable: false),
                ExpectedBinId = table.Column<Guid>(type: "uuid", nullable: true),
                ExpectedBinCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                SourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SourceFileId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceMappingId = table.Column<Guid>(type: "uuid", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintJobPartOutputSnapshots", x => x.Id);
                table.CheckConstraint("CK_PrintJobPartOutputSnapshots_ExpectedBin_Consistent", "(\"ExpectedBinId\" IS NULL AND \"ExpectedBinCode\" IS NULL) OR (\"ExpectedBinId\" IS NOT NULL AND \"ExpectedBinCode\" IS NOT NULL)");
                table.CheckConstraint("CK_PrintJobPartOutputSnapshots_Quantity_Positive", "\"QuantityPerPrint\" > 0");
                table.CheckConstraint("CK_PrintJobPartOutputSnapshots_Sequence_NonNegative", "\"Sequence\" >= 0");
                table.CheckConstraint("CK_PrintJobPartOutputSnapshots_Sku_Normalized", "\"Sku\" = UPPER(\"Sku\")");
                table.ForeignKey(
                    name: "FK_PrintJobPartOutputSnapshots_Bins_ExpectedBinId",
                    column: x => x.ExpectedBinId,
                    principalTable: "Bins",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrintJobPartOutputSnapshots_PartInventories_PartInventoryId",
                    column: x => x.PartInventoryId,
                    principalTable: "PartInventories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrintJobPartOutputSnapshots_PrintJobs_PrintJobId",
                    column: x => x.PrintJobId,
                    principalTable: "PrintJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_ActualBinId",
            table: "PartHarvestOutputSnapshots",
            column: "ActualBinId");

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_ExpectedBinId",
            table: "PartHarvestOutputSnapshots",
            column: "ExpectedBinId");

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_PartInventoryAdjustmentId",
            table: "PartHarvestOutputSnapshots",
            column: "PartInventoryAdjustmentId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_PartInventoryId",
            table: "PartHarvestOutputSnapshots",
            column: "PartInventoryId");

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_PrintJobId_Sequence",
            table: "PartHarvestOutputSnapshots",
            columns: new[] { "PrintJobId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PartHarvestOutputSnapshots_SourceMappingId",
            table: "PartHarvestOutputSnapshots",
            column: "SourceMappingId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobPartOutputSnapshots_ExpectedBinId",
            table: "PrintJobPartOutputSnapshots",
            column: "ExpectedBinId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobPartOutputSnapshots_PartInventoryId",
            table: "PrintJobPartOutputSnapshots",
            column: "PartInventoryId");

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobPartOutputSnapshots_PrintJobId_Sequence",
            table: "PrintJobPartOutputSnapshots",
            columns: new[] { "PrintJobId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintJobPartOutputSnapshots_SourceMappingId",
            table: "PrintJobPartOutputSnapshots",
            column: "SourceMappingId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PartHarvestOutputSnapshots");

        migrationBuilder.DropTable(
            name: "PrintJobPartOutputSnapshots");

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "PartInventories",
            type: "bytea",
            rowVersion: true,
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "Bins",
            type: "bytea",
            rowVersion: true,
            nullable: true);
    }
}
