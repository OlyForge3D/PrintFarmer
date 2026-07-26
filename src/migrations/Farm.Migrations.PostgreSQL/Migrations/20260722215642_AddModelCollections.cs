using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddModelCollections : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ModelCollections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                IsShared = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ModelCollections", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ModelCollectionMemberships",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ModelCollectionMemberships", x => x.Id);
                table.ForeignKey(
                    name: "FK_ModelCollectionMemberships_ModelCollections_CollectionId",
                    column: x => x.CollectionId,
                    principalTable: "ModelCollections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollectionMemberships_CollectionId_ModelId",
            table: "ModelCollectionMemberships",
            columns: new[] { "CollectionId", "ModelId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollectionMemberships_ModelId",
            table: "ModelCollectionMemberships",
            column: "ModelId");

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollectionMemberships_UpdatedAt",
            table: "ModelCollectionMemberships",
            column: "UpdatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollections_OwnerUserId",
            table: "ModelCollections",
            column: "OwnerUserId");

        migrationBuilder.CreateIndex(
            name: "IX_ModelCollections_UpdatedAt",
            table: "ModelCollections",
            column: "UpdatedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ModelCollectionMemberships");

        migrationBuilder.DropTable(
            name: "ModelCollections");
    }
}
