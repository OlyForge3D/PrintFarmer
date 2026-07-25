using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddCalibrationBlobCleanup : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CalibrationBlobCleanups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OpaqueStorageKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationBlobCleanups", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationBlobCleanups_CreatedAtUtc",
            table: "CalibrationBlobCleanups",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_CalibrationBlobCleanups_OpaqueStorageKey",
            table: "CalibrationBlobCleanups",
            column: "OpaqueStorageKey",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CalibrationBlobCleanups");
    }
}
