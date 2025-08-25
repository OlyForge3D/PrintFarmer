using Microsoft.EntityFrameworkCore.Migrations;
using Farm.Web.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Farm.Web.Server.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20250824223000_MoveMaxToModel")]
    public partial class MoveMaxToModel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ensure columns exist on Models
            migrationBuilder.Sql("ALTER TABLE Models ADD COLUMN MaxX REAL", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE Models ADD COLUMN MaxY REAL", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE Models ADD COLUMN MaxZ REAL", suppressTransaction: true);

            // If Printers had MaxX/MaxY/MaxZ, try to migrate values to the associated Model when possible
            // Note: SQLite lacks procedural SQL; this is a best-effort using simple UPDATE with join-like pattern
            migrationBuilder.Sql(@"
                UPDATE Models 
                SET MaxX = COALESCE(MaxX, (
                    SELECT p.MaxX FROM Printers p 
                    WHERE p.ModelId = Models.Id AND p.MaxX IS NOT NULL 
                    LIMIT 1
                )),
                    MaxY = COALESCE(MaxY, (
                    SELECT p.MaxY FROM Printers p 
                    WHERE p.ModelId = Models.Id AND p.MaxY IS NOT NULL 
                    LIMIT 1
                )),
                    MaxZ = COALESCE(MaxZ, (
                    SELECT p.MaxZ FROM Printers p 
                    WHERE p.ModelId = Models.Id AND p.MaxZ IS NOT NULL 
                    LIMIT 1
                ))
            ");

            // We intentionally do not drop columns from Printers to avoid data loss and SQLite table rebuild.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op to avoid data loss.
        }
    }
}
