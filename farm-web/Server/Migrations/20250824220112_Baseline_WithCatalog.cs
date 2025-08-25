using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Web.Server.Migrations
{
    /// <inheritdoc />
    public partial class Baseline_WithCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create catalog tables if they don't exist (SQLite)
            migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS Manufacturers (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT COLLATE NOCASE NOT NULL UNIQUE
            );");

            migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS Models (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT COLLATE NOCASE NOT NULL,
                ManufacturerId TEXT NOT NULL,
                FOREIGN KEY (ManufacturerId) REFERENCES Manufacturers(Id) ON DELETE CASCADE
            );");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS IX_Models_ManufacturerId_Name ON Models(ManufacturerId, Name);");

            // Augment existing Printers table with new columns if they don't exist
            migrationBuilder.Sql(@"ALTER TABLE Printers ADD COLUMN ManufacturerId TEXT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE Printers ADD COLUMN ModelId TEXT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE Printers ADD COLUMN MaxX REAL NULL;");
            migrationBuilder.Sql(@"ALTER TABLE Printers ADD COLUMN MaxY REAL NULL;");
            migrationBuilder.Sql(@"ALTER TABLE Printers ADD COLUMN MaxZ REAL NULL;");
            migrationBuilder.Sql(@"ALTER TABLE Printers ADD COLUMN DateAcquired TEXT NULL;");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS IX_Printers_ManufacturerId ON Printers(ManufacturerId);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS IX_Printers_ModelId ON Printers(ModelId);");

            // Ensure Spools table exists (it should from initial create); create if not present
            migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS Spools (
                Id TEXT NOT NULL PRIMARY KEY,
                Material TEXT NOT NULL,
                WeightGrams REAL NOT NULL,
                ColorHex TEXT NOT NULL,
                InUse INTEGER NOT NULL,
                AssignedPrinterId TEXT NULL
            );");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS IX_Spools_AssignedPrinterId ON Spools(AssignedPrinterId);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No down migration to avoid dropping existing user data
        }
    }
}
