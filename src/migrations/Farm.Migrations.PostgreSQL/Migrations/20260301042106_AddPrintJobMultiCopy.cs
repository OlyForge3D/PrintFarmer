using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddPrintJobMultiCopy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CompletedCopies",
            table: "PrintJobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "Copies",
            table: "PrintJobs",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<Guid>(
            name: "ProjectFileId",
            table: "PrintJobs",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CompletedCopies",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "Copies",
            table: "PrintJobs");

        migrationBuilder.DropColumn(
            name: "ProjectFileId",
            table: "PrintJobs");
    }
}
