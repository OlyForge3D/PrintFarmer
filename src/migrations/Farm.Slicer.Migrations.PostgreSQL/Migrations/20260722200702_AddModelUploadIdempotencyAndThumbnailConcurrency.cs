using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddModelUploadIdempotencyAndThumbnailConcurrency : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ClientUploadHash",
            schema: "slicer",
            table: "Models3D",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ClientUploadId",
            schema: "slicer",
            table: "Models3D",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_UploadedByUserId_ClientUploadId",
            schema: "slicer",
            table: "Models3D",
            columns: new[] { "UploadedByUserId", "ClientUploadId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Models3D_UploadedByUserId_ClientUploadId",
            schema: "slicer",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "ClientUploadHash",
            schema: "slicer",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "ClientUploadId",
            schema: "slicer",
            table: "Models3D");
    }
}
