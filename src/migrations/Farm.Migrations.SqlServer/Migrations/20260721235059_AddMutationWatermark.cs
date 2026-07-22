using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddMutationWatermark : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "LastMutationSequence",
            table: "UserTasks",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateTable(
            name: "MutationCounters",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Value = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MutationCounters", x => x.Id);
            });

        migrationBuilder.InsertData(
            table: "MutationCounters",
            columns: new[] { "Id", "Value" },
            values: new object[] { 1, 0L });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MutationCounters");

        migrationBuilder.DropColumn(
            name: "LastMutationSequence",
            table: "UserTasks");
    }
}
