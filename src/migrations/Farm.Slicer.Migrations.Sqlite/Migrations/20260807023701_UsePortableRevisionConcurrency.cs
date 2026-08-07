using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class UsePortableRevisionConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Models3D");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Models3D",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Models3D");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Models3D",
                type: "BLOB",
                rowVersion: true,
                nullable: true);
        }
    }
}
