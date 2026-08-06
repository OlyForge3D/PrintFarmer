using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalizePrintJobPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "PrintJobs",
                type: "int",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE [PrintJobs]
                SET [Priority] = CASE
                    WHEN [Priority] < 0 THEN 0
                    WHEN [Priority] > 3 THEN 3
                    ELSE [Priority]
                END;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PrintJobs_Priority",
                table: "PrintJobs",
                sql: "[Priority] >= 0 AND [Priority] <= 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PrintJobs_Priority",
                table: "PrintJobs");

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "PrintJobs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 1);
        }
    }
}
