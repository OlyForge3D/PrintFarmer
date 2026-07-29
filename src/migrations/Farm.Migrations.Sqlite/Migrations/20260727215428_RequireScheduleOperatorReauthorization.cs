using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class RequireScheduleOperatorReauthorization : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
                name: "InitiatingActorSubject",
                table: "JobSchedules",
                type: "TEXT",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256);

        migrationBuilder.AddColumn<int>(
            name: "RecurrenceInterval",
            table: "JobSchedules",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<bool>(
            name: "RequiresOperatorReauthorization",
            table: "JobSchedules",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.Sql(
            """
                UPDATE "JobSchedules"
                SET
                    "RecurrenceInterval" = 1,
                    "IsActive" = 0,
                    "IsPaused" = 1,
                    "RequiresOperatorReauthorization" = 1,
                    "InitiatingActorSubject" = NULL;
                """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
                """
                UPDATE "JobSchedules"
                SET "InitiatingActorSubject" = ''
                WHERE "InitiatingActorSubject" IS NULL;
                """);

        migrationBuilder.DropColumn(
            name: "RecurrenceInterval",
            table: "JobSchedules");

        migrationBuilder.DropColumn(
            name: "RequiresOperatorReauthorization",
            table: "JobSchedules");

        migrationBuilder.AlterColumn<string>(
            name: "InitiatingActorSubject",
            table: "JobSchedules",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: string.Empty,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 256,
            oldNullable: true);
    }
}
