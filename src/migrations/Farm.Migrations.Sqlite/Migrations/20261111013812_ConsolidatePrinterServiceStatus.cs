using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidatePrinterServiceStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresOperatorReview",
                table: "PrintJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE \"PrintJobs\" SET \"RequiresOperatorReview\" = 1 "
                + "WHERE \"AssignedPrinterId\" IN (SELECT \"Id\" FROM \"Printers\" WHERE \"ExpectedOfflineSinceUtc\" IS NOT NULL) "
                + "AND \"Status\" IN (1, 2, 3, 4);");
            migrationBuilder.Sql(
                "UPDATE \"Printers\" SET \"IsEnabled\" = 0 "
                + "WHERE \"ExpectedOfflineSinceUtc\" IS NOT NULL "
                + "AND NOT EXISTS (SELECT 1 FROM \"PrintJobs\" "
                + "WHERE \"PrintJobs\".\"AssignedPrinterId\" = \"Printers\".\"Id\" "
                + "AND \"PrintJobs\".\"Status\" IN (1, 2, 3, 4));");
            migrationBuilder.DropColumn(
                name: "ExpectedOfflineSinceUtc",
                table: "Printers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpectedOfflineSinceUtc",
                table: "Printers",
                type: "TEXT",
                nullable: true);
            migrationBuilder.Sql(
                "UPDATE \"Printers\" SET \"ExpectedOfflineSinceUtc\" = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') "
                + "WHERE \"Id\" IN (SELECT \"AssignedPrinterId\" FROM \"PrintJobs\" "
                + "WHERE \"RequiresOperatorReview\" = 1 AND \"AssignedPrinterId\" IS NOT NULL "
                + "AND \"Status\" IN (1, 2, 3, 4));");
            migrationBuilder.DropColumn(
                name: "RequiresOperatorReview",
                table: "PrintJobs");
        }
    }
}
