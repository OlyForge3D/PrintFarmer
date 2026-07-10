using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintedPartsInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HarvestOperationKey",
                table: "PrintJobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HarvestedAt",
                table: "PrintJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HarvestedByUserId",
                table: "PrintJobs",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HarvestedIntoBinId",
                table: "PrintJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BinId",
                table: "BarcodeScanLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PartInventoryId",
                table: "BarcodeScanLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Bins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PartInventories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ModelFileRef = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DefaultBinId = table.Column<Guid>(type: "uuid", nullable: true),
                    OnHand = table.Column<int>(type: "integer", nullable: false),
                    ReorderPoint = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartInventories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartInventories_Bins_DefaultBinId",
                        column: x => x.DefaultBinId,
                        principalTable: "Bins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PartInventoryAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartInventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: true),
                    Delta = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PrintJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartInventoryAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartInventoryAdjustments_Bins_BinId",
                        column: x => x.BinId,
                        principalTable: "Bins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PartInventoryAdjustments_PartInventories_PartInventoryId",
                        column: x => x.PartInventoryId,
                        principalTable: "PartInventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartInventoryAdjustments_PrintJobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PartOutputMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartInventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    GcodeFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    PrintProjectFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartOutputMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartOutputMappings_GcodeFiles_GcodeFileId",
                        column: x => x.GcodeFileId,
                        principalTable: "GcodeFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartOutputMappings_PartInventories_PartInventoryId",
                        column: x => x.PartInventoryId,
                        principalTable: "PartInventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartOutputMappings_PrintProjectFiles_PrintProjectFileId",
                        column: x => x.PrintProjectFileId,
                        principalTable: "PrintProjectFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BarcodeScanLogs_BinId",
                table: "BarcodeScanLogs",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_BarcodeScanLogs_PartInventoryId",
                table: "BarcodeScanLogs",
                column: "PartInventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Bins_Code",
                table: "Bins",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bins_IsActive",
                table: "Bins",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PartInventories_DefaultBinId",
                table: "PartInventories",
                column: "DefaultBinId");

            migrationBuilder.CreateIndex(
                name: "IX_PartInventories_IsActive",
                table: "PartInventories",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PartInventories_Sku",
                table: "PartInventories",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartInventoryAdjustments_BinId",
                table: "PartInventoryAdjustments",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_PartInventoryAdjustments_OperationKey",
                table: "PartInventoryAdjustments",
                column: "OperationKey",
                unique: true,
                filter: "\"OperationKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PartInventoryAdjustments_PartInventoryId",
                table: "PartInventoryAdjustments",
                column: "PartInventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PartInventoryAdjustments_PartInventoryId_CreatedAt",
                table: "PartInventoryAdjustments",
                columns: new[] { "PartInventoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PartInventoryAdjustments_PrintJobId",
                table: "PartInventoryAdjustments",
                column: "PrintJobId");

            migrationBuilder.CreateIndex(
                name: "IX_PartInventoryAdjustments_Reason",
                table: "PartInventoryAdjustments",
                column: "Reason");

            migrationBuilder.CreateIndex(
                name: "IX_PartOutputMappings_GcodeFileId",
                table: "PartOutputMappings",
                column: "GcodeFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PartOutputMappings_GcodeFileId_PartInventoryId",
                table: "PartOutputMappings",
                columns: new[] { "GcodeFileId", "PartInventoryId" },
                unique: true,
                filter: "\"GcodeFileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PartOutputMappings_PartInventoryId",
                table: "PartOutputMappings",
                column: "PartInventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PartOutputMappings_PrintProjectFileId",
                table: "PartOutputMappings",
                column: "PrintProjectFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PartOutputMappings_PrintProjectFileId_PartInventoryId",
                table: "PartOutputMappings",
                columns: new[] { "PrintProjectFileId", "PartInventoryId" },
                unique: true,
                filter: "\"PrintProjectFileId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PartInventoryAdjustments");

            migrationBuilder.DropTable(
                name: "PartOutputMappings");

            migrationBuilder.DropTable(
                name: "PartInventories");

            migrationBuilder.DropTable(
                name: "Bins");

            migrationBuilder.DropIndex(
                name: "IX_BarcodeScanLogs_BinId",
                table: "BarcodeScanLogs");

            migrationBuilder.DropIndex(
                name: "IX_BarcodeScanLogs_PartInventoryId",
                table: "BarcodeScanLogs");

            migrationBuilder.DropColumn(
                name: "HarvestOperationKey",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "HarvestedAt",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "HarvestedByUserId",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "HarvestedIntoBinId",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "BinId",
                table: "BarcodeScanLogs");

            migrationBuilder.DropColumn(
                name: "PartInventoryId",
                table: "BarcodeScanLogs");
        }
    }
}
