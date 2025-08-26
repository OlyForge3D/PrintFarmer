using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Web.Server.Migrations
{
    public partial class AddPrinterFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Backend",
                table: "Printers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ApiKey",
                table: "Printers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ManufacturerId",
                table: "Printers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ModelId",
                table: "Printers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateAcquired",
                table: "Printers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalHostName",
                table: "Printers",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Backend", table: "Printers");
            migrationBuilder.DropColumn(name: "ApiKey", table: "Printers");
            migrationBuilder.DropColumn(name: "ManufacturerId", table: "Printers");
            migrationBuilder.DropColumn(name: "ModelId", table: "Printers");
            migrationBuilder.DropColumn(name: "DateAcquired", table: "Printers");
            migrationBuilder.DropColumn(name: "OriginalHostName", table: "Printers");
        }
    }
}
