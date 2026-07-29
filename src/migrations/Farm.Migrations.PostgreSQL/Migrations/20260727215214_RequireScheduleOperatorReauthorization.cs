using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class RequireScheduleOperatorReauthorization : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
                name: "InitiatingActorSubject",
                table: "JobSchedules",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

        migrationBuilder.AddColumn<int>(
            name: "RecurrenceInterval",
            table: "JobSchedules",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<bool>(
            name: "RequiresOperatorReauthorization",
            table: "JobSchedules",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.Sql(
            """
                UPDATE "JobSchedules"
                SET
                    "RecurrenceInterval" = CASE
                        WHEN "RecurrenceInterval" < 1 THEN 1
                        ELSE "RecurrenceInterval"
                    END,
                    "IsActive" = CASE
                        WHEN "InitiatingActorSubject" ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                            THEN "IsActive"
                        ELSE FALSE
                    END,
                    "IsPaused" = CASE
                        WHEN "InitiatingActorSubject" ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                            THEN "IsPaused"
                        ELSE TRUE
                    END,
                    "RequiresOperatorReauthorization" = NOT COALESCE(
                        "InitiatingActorSubject" ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
                        FALSE
                    ),
                    "InitiatingActorSubject" = CASE
                        WHEN "InitiatingActorSubject" ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                            THEN "InitiatingActorSubject"
                        ELSE NULL
                    END;
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
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            defaultValue: string.Empty,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256,
            oldNullable: true);
    }
}
