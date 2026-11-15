using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
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
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE [PrintJobs] SET [RequiresOperatorReview] = 1, [Revision] = [Revision] + 1 "
                + "WHERE [AssignedPrinterId] IN (SELECT [Id] FROM [Printers] WHERE [ExpectedOfflineSinceUtc] IS NOT NULL) "
                + "AND [Status] IN (1, 2, 3, 4);");
            migrationBuilder.Sql(
                "UPDATE [Printers] SET [IsEnabled] = 0, [Revision] = [Revision] + 1 "
                + "WHERE [ExpectedOfflineSinceUtc] IS NOT NULL "
                + "AND NOT EXISTS (SELECT 1 FROM [PrintJobs] "
                + "WHERE [PrintJobs].[AssignedPrinterId] = [Printers].[Id] "
                + "AND [PrintJobs].[Status] IN (1, 2, 3, 4));");
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
                type: "datetime2",
                nullable: true);

            // Review markers do not retain the original printer or timestamp.
            // Restoring monitoring intent requires a pre-upgrade backup.
            migrationBuilder.DropColumn(
                name: "RequiresOperatorReview",
                table: "PrintJobs");
        }
    }
}
