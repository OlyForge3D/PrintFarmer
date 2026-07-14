using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class EnforceUniqueToolheadPrinterIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ;WITH ranked AS (
                SELECT
                    [Id],
                    ROW_NUMBER() OVER (
                        PARTITION BY [PrinterId], [Index]
                        ORDER BY
                            CASE WHEN [CurrentSpoolId] IS NOT NULL THEN 0 ELSE 1 END,
                            [UpdatedAt] DESC,
                            [Id] ASC
                    ) AS [row_number]
                FROM [Toolheads]
            )
            DELETE FROM ranked
            WHERE [row_number] > 1;
            """);

        migrationBuilder.CreateIndex(
            name: "UX_Toolheads_PrinterId_Index",
            table: "Toolheads",
            columns: new[] { "PrinterId", "Index" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_Toolheads_PrinterId_Index",
            table: "Toolheads");
    }
}
