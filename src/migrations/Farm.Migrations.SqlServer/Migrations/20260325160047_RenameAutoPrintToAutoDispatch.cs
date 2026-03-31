using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class RenameAutoPrintToAutoDispatch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "AutoPrintState",
            table: "Printers",
            newName: "AutoDispatchState");

        migrationBuilder.RenameColumn(
            name: "AutoPrintEnabled",
            table: "Printers",
            newName: "AutoDispatchEnabled");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "AutoDispatchState",
            table: "Printers",
            newName: "AutoPrintState");

        migrationBuilder.RenameColumn(
            name: "AutoDispatchEnabled",
            table: "Printers",
            newName: "AutoPrintEnabled");
    }
}
