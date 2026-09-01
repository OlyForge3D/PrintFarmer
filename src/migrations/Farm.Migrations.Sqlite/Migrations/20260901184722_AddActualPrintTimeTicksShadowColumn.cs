using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddActualPrintTimeTicksShadowColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ActualPrintTimeTicks",
                table: "PrintJobs",
                type: "INTEGER",
                nullable: true);

            // Backfill existing rows: ActualPrintTime is already stored as raw ticks (INTEGER)
            // via its value converter, so the new shadow column can be copied directly.
            migrationBuilder.Sql(
                """
                UPDATE "PrintJobs"
                SET "ActualPrintTimeTicks" = "ActualPrintTime"
                WHERE "ActualPrintTime" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualPrintTimeTicks",
                table: "PrintJobs");
        }
    }
}
