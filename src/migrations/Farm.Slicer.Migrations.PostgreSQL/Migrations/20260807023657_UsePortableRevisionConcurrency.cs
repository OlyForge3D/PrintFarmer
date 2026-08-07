using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class UsePortableRevisionConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "slicer",
                table: "Models3D");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                schema: "slicer",
                table: "Models3D",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "slicer",
                table: "Models3D");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "slicer",
                table: "Models3D",
                type: "bytea",
                rowVersion: true,
                nullable: true);
        }
    }
}
