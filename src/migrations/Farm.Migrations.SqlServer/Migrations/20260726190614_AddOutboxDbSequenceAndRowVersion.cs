using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddOutboxDbSequenceAndRowVersion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "QueueDispatchOutbox",
            type: "rowversion",
            rowVersion: true,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "OutboxSequenceStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                NextSequence = table.Column<long>(type: "bigint", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxSequenceStates", x => x.Id);
            });

        migrationBuilder.InsertData(
            table: "OutboxSequenceStates",
            columns: new[] { "Id", "NextSequence" },
            values: new object[] { 1, 0L });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OutboxSequenceStates");

        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "QueueDispatchOutbox");
    }
}
