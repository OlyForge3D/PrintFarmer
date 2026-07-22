using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddLibrarySyncJournal : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ConcurrencyToken",
            table: "Tags",
            type: "uniqueidentifier",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "Tags",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<Guid>(
            name: "ConcurrencyToken",
            table: "ModelCollections",
            type: "uniqueidentifier",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "ModelCollections",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "ModelCollectionMemberships",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateTable(
            name: "LibrarySyncChanges",
            columns: table => new
            {
                Revision = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                EntityType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Visibility = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibrarySyncChanges", x => x.Revision);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LibrarySyncChanges_EntityType_EntityId",
            table: "LibrarySyncChanges",
            columns: new[] { "EntityType", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_LibrarySyncChanges_OwnerUserId",
            table: "LibrarySyncChanges",
            column: "OwnerUserId");

        migrationBuilder.CreateIndex(
            name: "IX_LibrarySyncChanges_Timestamp",
            table: "LibrarySyncChanges",
            column: "Timestamp");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "LibrarySyncChanges");

        migrationBuilder.DropColumn(
            name: "ConcurrencyToken",
            table: "Tags");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "Tags");

        migrationBuilder.DropColumn(
            name: "ConcurrencyToken",
            table: "ModelCollections");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "ModelCollections");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "ModelCollectionMemberships");
    }
}
