using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddPrinterTags : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PrinterTag",
            columns: table => new
            {
                PrinterId = table.Column<Guid>(type: "uuid", nullable: false),
                TagsId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterTag", x => new { x.PrinterId, x.TagsId });
                table.ForeignKey(
                    name: "FK_PrinterTag_Printers_PrinterId",
                    column: x => x.PrinterId,
                    principalTable: "Printers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrinterTag_Tag_TagsId",
                    column: x => x.TagsId,
                    principalTable: "Tag",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PrinterTag_TagsId",
            table: "PrinterTag",
            column: "TagsId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PrinterTag");
    }
}
