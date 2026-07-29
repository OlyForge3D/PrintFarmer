using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class RequireScheduleOperatorReauthorization : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
                name: "InitiatingActorSubject",
                table: "JobSchedules",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

        migrationBuilder.AddColumn<int>(
            name: "RecurrenceInterval",
            table: "JobSchedules",
            type: "int",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<bool>(
            name: "RequiresOperatorReauthorization",
            table: "JobSchedules",
            type: "bit",
            nullable: false,
            defaultValue: true);

        migrationBuilder.Sql(
            """
                UPDATE [JobSchedules]
                SET
                    [RecurrenceInterval] = CASE
                        WHEN [RecurrenceInterval] < 1 THEN 1
                        ELSE [RecurrenceInterval]
                    END,
                    [IsActive] = CASE
                        WHEN TRY_CONVERT(uniqueidentifier, [InitiatingActorSubject]) IS NOT NULL
                            THEN [IsActive]
                        ELSE 0
                    END,
                    [IsPaused] = CASE
                        WHEN TRY_CONVERT(uniqueidentifier, [InitiatingActorSubject]) IS NOT NULL
                            THEN [IsPaused]
                        ELSE 1
                    END,
                    [RequiresOperatorReauthorization] = CASE
                        WHEN TRY_CONVERT(uniqueidentifier, [InitiatingActorSubject]) IS NOT NULL
                            THEN 0
                        ELSE 1
                    END,
                    [InitiatingActorSubject] = CASE
                        WHEN TRY_CONVERT(uniqueidentifier, [InitiatingActorSubject]) IS NOT NULL
                            THEN [InitiatingActorSubject]
                        ELSE NULL
                    END;
                """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
                """
                UPDATE [JobSchedules]
                SET [InitiatingActorSubject] = ''
                WHERE [InitiatingActorSubject] IS NULL;
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
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            defaultValue: string.Empty,
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256,
            oldNullable: true);
    }
}
