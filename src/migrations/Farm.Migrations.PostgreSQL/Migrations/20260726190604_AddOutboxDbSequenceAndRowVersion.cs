using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddOutboxDbSequenceAndRowVersion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "QueueDispatchOutbox",
            type: "bytea",
            maxLength: 16,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "OutboxSequenceStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                NextSequence = table.Column<long>(type: "bigint", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxSequenceStates", x => x.Id);
            });

        migrationBuilder.InsertData(
            table: "OutboxSequenceStates",
            columns: new[] { "Id", "NextSequence", "RowVersion" },
            values: new object[] { 1, 0L, null });
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
