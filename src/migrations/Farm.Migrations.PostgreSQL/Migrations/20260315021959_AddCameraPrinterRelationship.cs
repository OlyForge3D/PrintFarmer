using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraPrinterRelationship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CameraType",
                table: "Cameras",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "Cameras",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HealthMessage",
                table: "Cameras",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthStatus",
                table: "Cameras",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHealthCheck",
                table: "Cameras",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PrinterId",
                table: "Cameras",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Cameras",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_PrinterId",
                table: "Cameras",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_Source",
                table: "Cameras",
                column: "Source");

            migrationBuilder.AddForeignKey(
                name: "FK_Cameras_Printers_PrinterId",
                table: "Cameras",
                column: "PrinterId",
                principalTable: "Printers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cameras_Printers_PrinterId",
                table: "Cameras");

            migrationBuilder.DropIndex(
                name: "IX_Cameras_PrinterId",
                table: "Cameras");

            migrationBuilder.DropIndex(
                name: "IX_Cameras_Source",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "CameraType",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "HealthMessage",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "HealthStatus",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "LastHealthCheck",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "PrinterId",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Cameras");
        }
    }
}
