using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class Wave9_BedTypeAndDispatchDefaults : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "BedTypeId",
            table: "Printers",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "UseModelDispatchDefaults",
            table: "Printers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "DefaultAutoDispatchState",
            table: "PrinterModels",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<Guid>(
            name: "DefaultBedTypeId",
            table: "PrinterModels",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "DefaultStartBehavior",
            table: "PrinterModels",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "BedTypes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BedTypes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Printers_BedTypeId",
            table: "Printers",
            column: "BedTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_PrinterModels_DefaultBedTypeId",
            table: "PrinterModels",
            column: "DefaultBedTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_BedTypes_Name",
            table: "BedTypes",
            column: "Name",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterModels_BedTypes_DefaultBedTypeId",
            table: "PrinterModels",
            column: "DefaultBedTypeId",
            principalTable: "BedTypes",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_Printers_BedTypes_BedTypeId",
            table: "Printers",
            column: "BedTypeId",
            principalTable: "BedTypes",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_PrinterModels_BedTypes_DefaultBedTypeId",
            table: "PrinterModels");

        migrationBuilder.DropForeignKey(
            name: "FK_Printers_BedTypes_BedTypeId",
            table: "Printers");

        migrationBuilder.DropTable(
            name: "BedTypes");

        migrationBuilder.DropIndex(
            name: "IX_Printers_BedTypeId",
            table: "Printers");

        migrationBuilder.DropIndex(
            name: "IX_PrinterModels_DefaultBedTypeId",
            table: "PrinterModels");

        migrationBuilder.DropColumn(
            name: "BedTypeId",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "UseModelDispatchDefaults",
            table: "Printers");

        migrationBuilder.DropColumn(
            name: "DefaultAutoDispatchState",
            table: "PrinterModels");

        migrationBuilder.DropColumn(
            name: "DefaultBedTypeId",
            table: "PrinterModels");

        migrationBuilder.DropColumn(
            name: "DefaultStartBehavior",
            table: "PrinterModels");
    }
}
