using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerDisableSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DisableSource",
                table: "Workers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Backfill: attribute existing disables from their reason text, which is all the
            // information rows written before this column carry. Ordering matters — the broad
            // "anything else is an administrator" pass runs first and the two automatic patterns
            // then correct themselves. Without this every existing administrator ban would read
            // as None, and the next registration would silently lift it.
            migrationBuilder.Sql(
                """
                UPDATE "Workers"
                SET "DisableSource" = 1
                WHERE "IsDisabled" = 1
                  AND "DisabledReason" IS NOT NULL
                  AND TRIM("DisabledReason") <> ''
                  AND "DisabledReason" <> 'Slicer service deregistered'
                  AND "DisabledReason" NOT LIKE 'Circuit breaker:%';
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Workers"
                SET "DisableSource" = 2
                WHERE "IsDisabled" = 1
                  AND "DisabledReason" = 'Slicer service deregistered';
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Workers"
                SET "DisableSource" = 3
                WHERE "IsDisabled" = 1
                  AND "DisabledReason" LIKE 'Circuit breaker:%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisableSource",
                table: "Workers");
        }
    }
}
