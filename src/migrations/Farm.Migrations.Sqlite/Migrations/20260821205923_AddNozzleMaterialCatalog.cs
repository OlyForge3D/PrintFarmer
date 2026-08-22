using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddNozzleMaterialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create the new NozzleMaterials catalog table.
            migrationBuilder.CreateTable(
                name: "NozzleMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsHardened = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DefaultMaxTemp = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 500),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NozzleMaterials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NozzleMaterials_Name",
                table: "NozzleMaterials",
                column: "Name",
                unique: true);

            // 2. Seed one built-in NozzleMaterial row per NozzleType enum member (Brass=0,
            //    HardenedSteel=1, StainlessSteel=2, TungstenCarbide=3, Abrasive=4, Diamond=5,
            //    Ruby=6, PlatedCopper=7, ToolSteel=8). Unknown=99 is not seeded, as it represents
            //    "no material assigned" rather than a real one.
            migrationBuilder.Sql(@"
                INSERT INTO ""NozzleMaterials"" (""Id"", ""Name"", ""IsHardened"", ""DefaultMaxTemp"", ""IsBuiltIn"", ""Description"")
                VALUES
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000001', 'Brass', 0, 260, 1, 'Standard brass nozzle - not abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000002', 'HardenedSteel', 1, 300, 1, 'Hardened steel nozzle - abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000003', 'StainlessSteel', 0, 300, 1, 'Stainless steel nozzle - food safe, not abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000004', 'TungstenCarbide', 1, 500, 1, 'Tungsten carbide nozzle - highly abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000005', 'Abrasive', 1, 500, 1, 'Generic abrasion-resistant nozzle material'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000006', 'Diamond', 1, 500, 1, 'Diamond-tipped nozzle - extreme abrasion resistance combined with high thermal conductivity'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000007', 'Ruby', 1, 300, 1, 'Ruby-tipped nozzle in a brass body - abrasion resistant while retaining good thermal conductivity'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000008', 'PlatedCopper', 0, 300, 1, 'Plated copper nozzle - excellent thermal conductivity for high-flow printing, not abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000009', 'ToolSteel', 1, 500, 1, 'Tool steel nozzle - abrasion resistant with high temperature tolerance');
            ");

            // 3. Add the new FK column as nullable so we can backfill it from the legacy
            //    NozzleType column before making it required.
            migrationBuilder.AddColumn<Guid>(
                name: "NozzleMaterialId",
                table: "NozzleModelDefinitions",
                type: "TEXT",
                nullable: true);

            // 4. Backfill every existing NozzleModelDefinition row's NozzleMaterialId from its
            //    legacy NozzleType enum value, falling back to Brass for unrecognized/Unknown values.
            migrationBuilder.Sql(@"
                UPDATE ""NozzleModelDefinitions""
                SET ""NozzleMaterialId"" = (
                    SELECT nm.""Id"" FROM ""NozzleMaterials"" nm
                    WHERE nm.""Name"" = CASE ""NozzleModelDefinitions"".""NozzleType""
                        WHEN 0 THEN 'Brass'
                        WHEN 1 THEN 'HardenedSteel'
                        WHEN 2 THEN 'StainlessSteel'
                        WHEN 3 THEN 'TungstenCarbide'
                        WHEN 4 THEN 'Abrasive'
                        WHEN 5 THEN 'Diamond'
                        WHEN 6 THEN 'Ruby'
                        WHEN 7 THEN 'PlatedCopper'
                        WHEN 8 THEN 'ToolSteel'
                        ELSE 'Brass'
                    END
                );
            ");

            // 5. Drop the legacy enum column now that every row has been backfilled.
            migrationBuilder.DropColumn(
                name: "NozzleType",
                table: "NozzleModelDefinitions");

            // 6. Make the new FK column required now that it is fully populated.
            migrationBuilder.AlterColumn<Guid>(
                name: "NozzleMaterialId",
                table: "NozzleModelDefinitions",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NozzleModelDefinitions_NozzleMaterialId",
                table: "NozzleModelDefinitions",
                column: "NozzleMaterialId");

            migrationBuilder.AddForeignKey(
                name: "FK_NozzleModelDefinitions_NozzleMaterials_NozzleMaterialId",
                table: "NozzleModelDefinitions",
                column: "NozzleMaterialId",
                principalTable: "NozzleMaterials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NozzleModelDefinitions_NozzleMaterials_NozzleMaterialId",
                table: "NozzleModelDefinitions");

            migrationBuilder.AddColumn<int>(
                name: "NozzleType",
                table: "NozzleModelDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(@"
                UPDATE ""NozzleModelDefinitions""
                SET ""NozzleType"" = (
                    SELECT CASE nm.""Name""
                        WHEN 'Brass' THEN 0
                        WHEN 'HardenedSteel' THEN 1
                        WHEN 'StainlessSteel' THEN 2
                        WHEN 'TungstenCarbide' THEN 3
                        WHEN 'Abrasive' THEN 4
                        WHEN 'Diamond' THEN 5
                        WHEN 'Ruby' THEN 6
                        WHEN 'PlatedCopper' THEN 7
                        WHEN 'ToolSteel' THEN 8
                        ELSE 99
                    END
                    FROM ""NozzleMaterials"" nm
                    WHERE nm.""Id"" = ""NozzleModelDefinitions"".""NozzleMaterialId""
                );
            ");

            migrationBuilder.DropTable(
                name: "NozzleMaterials");

            migrationBuilder.DropIndex(
                name: "IX_NozzleModelDefinitions_NozzleMaterialId",
                table: "NozzleModelDefinitions");

            migrationBuilder.DropColumn(
                name: "NozzleMaterialId",
                table: "NozzleModelDefinitions");
        }
    }
}
