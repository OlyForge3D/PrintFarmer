using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddCalibrationChangeFeedAllocator : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CalibrationChangeFeedStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                LastSequence = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalibrationChangeFeedStates", x => x.Id);
            });

        migrationBuilder.InsertData(
            table: "CalibrationChangeFeedStates",
            columns: new[] { "Id", "LastSequence" },
            values: new object[] { 1, 0L });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CalibrationChangeFeedStates");
    }
}
