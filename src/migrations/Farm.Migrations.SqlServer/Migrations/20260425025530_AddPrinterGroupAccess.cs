using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddPrinterGroupAccess : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PrinterGroupAccesses",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PrinterGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AccessLevel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterGroupAccesses", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterGroupAccesses_PrinterGroups_PrinterGroupId",
                    column: x => x.PrinterGroupId,
                    principalTable: "PrinterGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrinterGroupAccesses_Roles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PrinterGroupAccesses_PrinterGroupId_RoleId_AccessLevel",
            table: "PrinterGroupAccesses",
            columns: new[] { "PrinterGroupId", "RoleId", "AccessLevel" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterGroupAccesses_RoleId",
            table: "PrinterGroupAccesses",
            column: "RoleId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PrinterGroupAccesses");
    }
}
