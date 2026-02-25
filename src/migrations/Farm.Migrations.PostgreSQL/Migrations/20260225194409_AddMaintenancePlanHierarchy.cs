using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenancePlanHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    Supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    InStock = table.Column<int>(type: "integer", nullable: false),
                    MinimumStock = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceComponents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenancePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PrinterId = table.Column<Guid>(type: "uuid", nullable: true),
                    PrinterModelId = table.Column<Guid>(type: "uuid", nullable: true),
                    ManufacturerId = table.Column<Guid>(type: "uuid", nullable: true),
                    MotionType = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenancePlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenancePlans_Manufacturers_ManufacturerId",
                        column: x => x.ManufacturerId,
                        principalTable: "Manufacturers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MaintenancePlans_PrinterModels_PrinterModelId",
                        column: x => x.PrinterModelId,
                        principalTable: "PrinterModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MaintenancePlans_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MaintenancePlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IntervalHours = table.Column<double>(type: "double precision", nullable: true),
                    IntervalDays = table.Column<int>(type: "integer", nullable: true),
                    EstimatedDurationMinutes = table.Column<int>(type: "integer", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceTasks_MaintenancePlans_MaintenancePlanId",
                        column: x => x.MaintenancePlanId,
                        principalTable: "MaintenancePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceTaskComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MaintenanceTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaintenanceComponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceTaskComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceTaskComponents_MaintenanceComponents_Maintenance~",
                        column: x => x.MaintenanceComponentId,
                        principalTable: "MaintenanceComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceTaskComponents_MaintenanceTasks_MaintenanceTaskId",
                        column: x => x.MaintenanceTaskId,
                        principalTable: "MaintenanceTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceComponents_Category",
                table: "MaintenanceComponents",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceComponents_Name",
                table: "MaintenanceComponents",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenancePlans_IsActive",
                table: "MaintenancePlans",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenancePlans_ManufacturerId",
                table: "MaintenancePlans",
                column: "ManufacturerId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenancePlans_PrinterId",
                table: "MaintenancePlans",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenancePlans_PrinterModelId",
                table: "MaintenancePlans",
                column: "PrinterModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTaskComponents_MaintenanceComponentId",
                table: "MaintenanceTaskComponents",
                column: "MaintenanceComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTaskComponents_MaintenanceTaskId_MaintenanceComp~",
                table: "MaintenanceTaskComponents",
                columns: new[] { "MaintenanceTaskId", "MaintenanceComponentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTasks_IsActive",
                table: "MaintenanceTasks",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTasks_MaintenancePlanId",
                table: "MaintenanceTasks",
                column: "MaintenancePlanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceTaskComponents");

            migrationBuilder.DropTable(
                name: "MaintenanceComponents");

            migrationBuilder.DropTable(
                name: "MaintenanceTasks");

            migrationBuilder.DropTable(
                name: "MaintenancePlans");
        }
    }
}
