using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddPrintJobCostFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "ActualCost",
            table: "PrintJobStatistics",
            type: "decimal(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "EstimatedCost",
            table: "PrintJobStatistics",
            type: "decimal(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "ActualCost",
            table: "PrintJobs",
            type: "decimal(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "EstimatedCost",
            table: "PrintJobs",
            type: "decimal(18,2)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ActualCost",
            table: "PrintJobStatistics");

        migrationBuilder.DropColumn(
            name: "EstimatedCost",
            table: "PrintJobStatistics");

        migrationBuilder.DropColumn(
            name: "ActualCost",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "EstimatedCost",
            table: "PrintJobs");
    }
}
