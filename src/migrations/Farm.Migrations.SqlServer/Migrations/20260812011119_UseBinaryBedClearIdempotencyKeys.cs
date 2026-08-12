using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class UseBinaryBedClearIdempotencyKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_BedClearCommandRecords_Printer_Key",
                table: "BedClearCommandRecords");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                table: "BedClearCommandRecords",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512);

            migrationBuilder.CreateIndex(
                name: "UX_BedClearCommandRecords_Printer_Key",
                table: "BedClearCommandRecords",
                columns: new[] { "PrinterId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_BedClearCommandRecords_Printer_Key",
                table: "BedClearCommandRecords");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                table: "BedClearCommandRecords",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "UX_BedClearCommandRecords_Printer_Key",
                table: "BedClearCommandRecords",
                columns: new[] { "PrinterId", "IdempotencyKey" },
                unique: true);
        }
    }
}
