using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class EnforceNormalizedMachineModelProfileNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MachineModelProfiles_Name_SlicerType",
                table: "MachineModelProfiles");

            migrationBuilder.AddColumn<string>(
                name: "NameNormalized",
                table: "MachineModelProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: string.Empty);

            migrationBuilder.Sql(
                """
                UPDATE "MachineModelProfiles"
                SET "NameNormalized" = "Name";
                """);

            migrationBuilder.AlterColumn<string>(
                name: "NameNormalized",
                table: "MachineModelProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256,
                oldDefaultValue: string.Empty);

            migrationBuilder.CreateIndex(
                name: "IX_MachineModelProfiles_Name_SlicerType",
                table: "MachineModelProfiles",
                columns: new[] { "NameNormalized", "SlicerType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MachineModelProfiles_Name_SlicerType",
                table: "MachineModelProfiles");

            migrationBuilder.DropColumn(
                name: "NameNormalized",
                table: "MachineModelProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_MachineModelProfiles_Name_SlicerType",
                table: "MachineModelProfiles",
                columns: new[] { "Name", "SlicerType" },
                unique: true);
        }
    }
}
