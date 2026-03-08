using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PrinterGroupId",
                table: "Printers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PrinterGroupId",
                table: "GcodeFiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedDate",
                table: "DispatchSettings",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "LoadBalancingStrategy",
                table: "DispatchSettings",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedDate",
                table: "DispatchSettings",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedDate",
                table: "DispatchLogs",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "DispatchMode",
                table: "DispatchLogs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "DispatchLogs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DispatchedByUserId",
                table: "DispatchLogs",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "DispatchLogs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScoringDetails",
                table: "DispatchLogs",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "DispatchLogs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedDate",
                table: "DispatchLogs",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "PrinterGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterGroups", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "DispatchSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedDate", "LoadBalancingStrategy", "UpdatedDate" },
                values: new object[] { new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "BestFit", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "IX_Printers_PrinterGroupId",
                table: "Printers",
                column: "PrinterGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GcodeFiles_PrinterGroupId",
                table: "GcodeFiles",
                column: "PrinterGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchLogs_DispatchedAt",
                table: "DispatchLogs",
                column: "DispatchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterGroups_Name",
                table: "PrinterGroups",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_GcodeFiles_PrinterGroups_PrinterGroupId",
                table: "GcodeFiles",
                column: "PrinterGroupId",
                principalTable: "PrinterGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_PrinterGroups_PrinterGroupId",
                table: "Printers",
                column: "PrinterGroupId",
                principalTable: "PrinterGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GcodeFiles_PrinterGroups_PrinterGroupId",
                table: "GcodeFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_Printers_PrinterGroups_PrinterGroupId",
                table: "Printers");

            migrationBuilder.DropTable(
                name: "PrinterGroups");

            migrationBuilder.DropIndex(
                name: "IX_Printers_PrinterGroupId",
                table: "Printers");

            migrationBuilder.DropIndex(
                name: "IX_GcodeFiles_PrinterGroupId",
                table: "GcodeFiles");

            migrationBuilder.DropIndex(
                name: "IX_DispatchLogs_DispatchedAt",
                table: "DispatchLogs");

            migrationBuilder.DropColumn(
                name: "PrinterGroupId",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "PrinterGroupId",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "CreatedDate",
                table: "DispatchSettings");

            migrationBuilder.DropColumn(
                name: "LoadBalancingStrategy",
                table: "DispatchSettings");

            migrationBuilder.DropColumn(
                name: "UpdatedDate",
                table: "DispatchSettings");

            migrationBuilder.DropColumn(
                name: "CreatedDate",
                table: "DispatchLogs");

            migrationBuilder.DropColumn(
                name: "DispatchMode",
                table: "DispatchLogs");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "DispatchLogs");

            migrationBuilder.DropColumn(
                name: "DispatchedByUserId",
                table: "DispatchLogs");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "DispatchLogs");

            migrationBuilder.DropColumn(
                name: "ScoringDetails",
                table: "DispatchLogs");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "DispatchLogs");

            migrationBuilder.DropColumn(
                name: "UpdatedDate",
                table: "DispatchLogs");
        }
    }
}
