using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Web.Server.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Manufacturers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Manufacturers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpoolmanConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpoolmanConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MaxX = table.Column<double>(type: "REAL", nullable: true),
                    MaxY = table.Column<double>(type: "REAL", nullable: true),
                    MaxZ = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models_Manufacturers_ManufacturerId",
                        column: x => x.ManufacturerId,
                        principalTable: "Manufacturers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Printers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ServerUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OriginalServerUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Backend = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    ManufacturerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DateAcquired = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Printers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Printers_Manufacturers_ManufacturerId",
                        column: x => x.ManufacturerId,
                        principalTable: "Manufacturers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Printers_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Spools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Material = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WeightGrams = table.Column<double>(type: "REAL", nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    InUse = table.Column<bool>(type: "INTEGER", nullable: false),
                    AssignedPrinterId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Spools_Printers_AssignedPrinterId",
                        column: x => x.AssignedPrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Manufacturers_Name",
                table: "Manufacturers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models_ManufacturerId_Name",
                table: "Models",
                columns: new[] { "ManufacturerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Printers_ManufacturerId",
                table: "Printers",
                column: "ManufacturerId");

            migrationBuilder.CreateIndex(
                name: "IX_Printers_ModelId",
                table: "Printers",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Spools_AssignedPrinterId",
                table: "Spools",
                column: "AssignedPrinterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpoolmanConfigs");

            migrationBuilder.DropTable(
                name: "Spools");

            migrationBuilder.DropTable(
                name: "Printers");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "Manufacturers");
        }
    }
}
