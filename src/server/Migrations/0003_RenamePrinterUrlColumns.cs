using Microsoft.EntityFrameworkCore.Migrations;

namespace Farm.Web.Server.Migrations
{
    public partial class RenamePrinterUrlColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename MoonrakerUrl -> ServerUrl if column exists
            try { migrationBuilder.RenameColumn(name: "MoonrakerUrl", table: "Printers", newName: "ServerUrl"); } catch { }

            // If OriginalHostName exists, rename to OriginalServerUrl, otherwise add column
            try { migrationBuilder.RenameColumn(name: "OriginalHostName", table: "Printers", newName: "OriginalServerUrl"); }
            catch { migrationBuilder.AddColumn<string>(name: "OriginalServerUrl", table: "Printers", type: "TEXT", nullable: true); }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse rename
            try { migrationBuilder.RenameColumn(name: "ServerUrl", table: "Printers", newName: "MoonrakerUrl"); } catch { }
            try { migrationBuilder.RenameColumn(name: "OriginalServerUrl", table: "Printers", newName: "OriginalHostName"); } catch { migrationBuilder.DropColumn(name: "OriginalServerUrl", table: "Printers"); }
        }
    }
}
