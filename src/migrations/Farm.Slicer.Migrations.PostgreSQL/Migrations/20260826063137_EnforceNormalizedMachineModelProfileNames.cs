using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class EnforceNormalizedMachineModelProfileNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MachineModelProfiles_Name_SlicerType",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.AddColumn<string>(
                name: "NameNormalized",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.Sql(
                """
                UPDATE "slicer"."MachineModelProfiles"
                SET "NameNormalized" = "Name";
                """);

            migrationBuilder.AlterColumn<string>(
                name: "NameNormalized",
                schema: "slicer",
                table: "MachineModelProfiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldDefaultValue: string.Empty);

            migrationBuilder.CreateIndex(
                name: "IX_MachineModelProfiles_Name_SlicerType",
                schema: "slicer",
                table: "MachineModelProfiles",
                columns: new[] { "NameNormalized", "SlicerType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MachineModelProfiles_Name_SlicerType",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "NameNormalized",
                schema: "slicer",
                table: "MachineModelProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_MachineModelProfiles_Name_SlicerType",
                schema: "slicer",
                table: "MachineModelProfiles",
                columns: new[] { "Name", "SlicerType" },
                unique: true);
        }
    }
}
