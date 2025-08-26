using Microsoft.EntityFrameworkCore.Migrations;

namespace Farm.Web.Server.Migrations
{
    public partial class RenamePrinterColumns_Final : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ensure ServerUrl exists, prefer rename from MoonrakerUrl
            try { migrationBuilder.RenameColumn(name: "MoonrakerUrl", table: "Printers", newName: "ServerUrl"); } catch { }
            try { migrationBuilder.AddColumn<string>(name: "ServerUrl", table: "Printers", type: "TEXT", nullable: true); } catch { }
            // Ensure OriginalServerUrl exists, prefer rename from OriginalHostName
            try { migrationBuilder.RenameColumn(name: "OriginalHostName", table: "Printers", newName: "OriginalServerUrl"); } catch { }
            try { migrationBuilder.AddColumn<string>(name: "OriginalServerUrl", table: "Printers", type: "TEXT", nullable: true); } catch { }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            try { migrationBuilder.RenameColumn(name: "ServerUrl", table: "Printers", newName: "MoonrakerUrl"); } catch { }
            try { migrationBuilder.RenameColumn(name: "OriginalServerUrl", table: "Printers", newName: "OriginalHostName"); } catch { migrationBuilder.DropColumn(name: "OriginalServerUrl", table: "Printers"); }
        }
    }
}
