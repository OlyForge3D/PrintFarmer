using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSliceJobClaimIncarnation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                schema: "slicer",
                table: "SliceJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                schema: "slicer",
                table: "Artifacts",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimToken",
                schema: "slicer",
                table: "SliceJobs");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                schema: "slicer",
                table: "Artifacts");
        }
    }
}
