using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddJobCostTrackingFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "CostCalculatedAt",
            table: "PrintJobs",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "EnergyCostUsd",
            table: "PrintJobs",
            type: "decimal(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "LaborCostUsd",
            table: "PrintJobs",
            type: "decimal(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "MachineTimeCostUsd",
            table: "PrintJobs",
            type: "decimal(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "MaterialCostUsd",
            table: "PrintJobs",
            type: "decimal(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "TotalCostUsd",
            table: "PrintJobs",
            type: "decimal(18,2)",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "MachineHourlyRate",
            table: "Printers",
            type: "decimal(18,2)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CostCalculatedAt",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "EnergyCostUsd",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "LaborCostUsd",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "MachineTimeCostUsd",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "MaterialCostUsd",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "TotalCostUsd",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "MachineHourlyRate",
            table: "Printers");
    }
}
