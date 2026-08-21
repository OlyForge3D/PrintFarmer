using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsHardened = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DefaultMaxTemp = table.Column<int>(type: "integer", nullable: false, defaultValue: 500),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
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

            // 2. Seed one built-in NozzleMaterial row per existing NozzleType enum member
            //    (Brass=0, HardenedSteel=1, StainlessSteel=2, TungstenCarbide=3, Abrasive=4).
            //    Note: the enum currently defines only these 5 members (plus Unknown=99, which
            //    is not seeded as it represents "no material assigned" rather than a real one).
            migrationBuilder.Sql(@"
                INSERT INTO ""NozzleMaterials"" (""Id"", ""Name"", ""IsHardened"", ""DefaultMaxTemp"", ""IsBuiltIn"", ""Description"")
                VALUES
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000001', 'Brass', false, 260, true, 'Standard brass nozzle - not abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000002', 'HardenedSteel', true, 300, true, 'Hardened steel nozzle - abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000003', 'StainlessSteel', false, 300, true, 'Stainless steel nozzle - food safe, not abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000004', 'TungstenCarbide', true, 500, true, 'Tungsten carbide nozzle - highly abrasion resistant'),
                    ('9f5a1c1e-0001-4a1a-8c1a-000000000005', 'Abrasive', true, 500, true, 'Generic abrasion-resistant nozzle material');
            ");

            // 3. Add the new FK column as nullable so we can backfill it from the legacy
            //    NozzleType column before making it required.
            migrationBuilder.AddColumn<Guid>(
                name: "NozzleMaterialId",
                table: "NozzleModelDefinitions",
                type: "uuid",
                nullable: true);

            // 4. Backfill every existing NozzleModelDefinition row's NozzleMaterialId from its
            //    legacy NozzleType enum value, falling back to Brass for unrecognized/Unknown values.
            migrationBuilder.Sql(@"
                UPDATE ""NozzleModelDefinitions"" nmd
                SET ""NozzleMaterialId"" = nm.""Id""
                FROM ""NozzleMaterials"" nm
                WHERE nm.""Name"" = CASE nmd.""NozzleType""
                    WHEN 0 THEN 'Brass'
                    WHEN 1 THEN 'HardenedSteel'
                    WHEN 2 THEN 'StainlessSteel'
                    WHEN 3 THEN 'TungstenCarbide'
                    WHEN 4 THEN 'Abrasive'
                    ELSE 'Brass'
                END;
            ");

            // 5. Drop the legacy enum column now that every row has been backfilled.
            migrationBuilder.DropColumn(
                name: "NozzleType",
                table: "NozzleModelDefinitions");

            // 6. Make the new FK column required now that it is fully populated.
            migrationBuilder.AlterColumn<Guid>(
                name: "NozzleMaterialId",
                table: "NozzleModelDefinitions",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
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
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(@"
                UPDATE ""NozzleModelDefinitions"" nmd
                SET ""NozzleType"" = CASE nm.""Name""
                    WHEN 'Brass' THEN 0
                    WHEN 'HardenedSteel' THEN 1
                    WHEN 'StainlessSteel' THEN 2
                    WHEN 'TungstenCarbide' THEN 3
                    WHEN 'Abrasive' THEN 4
                    ELSE 99
                END
                FROM ""NozzleMaterials"" nm
                WHERE nm.""Id"" = nmd.""NozzleMaterialId"";
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
