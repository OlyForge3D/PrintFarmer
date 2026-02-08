using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintProjectTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrintProjectTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DefaultPriority = table.Column<int>(type: "int", nullable: false),
                    DefaultNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsSystemTemplate = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintProjectTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrintProjectTemplateFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrintProjectTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FileNamePattern = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ColorRequirement = table.Column<int>(type: "int", nullable: false),
                    MaterialRequirement = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PrintCount = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintProjectTemplateFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintProjectTemplateFiles_PrintProjectTemplates_PrintProjectTemplateId",
                        column: x => x.PrintProjectTemplateId,
                        principalTable: "PrintProjectTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectTemplateFiles_PrintProjectTemplateId",
                table: "PrintProjectTemplateFiles",
                column: "PrintProjectTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectTemplateFiles_SortOrder",
                table: "PrintProjectTemplateFiles",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectTemplates_Category",
                table: "PrintProjectTemplates",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectTemplates_Name",
                table: "PrintProjectTemplates",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PrintProjectTemplates_SortOrder",
                table: "PrintProjectTemplates",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintProjectTemplateFiles");

            migrationBuilder.DropTable(
                name: "PrintProjectTemplates");
        }
    }
}
