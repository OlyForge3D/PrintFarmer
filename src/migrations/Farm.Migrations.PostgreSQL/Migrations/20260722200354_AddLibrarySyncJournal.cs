using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddLibrarySyncJournal : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ConcurrencyToken",
            table: "Tag",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "Tag",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<Guid>(
            name: "ConcurrencyToken",
            table: "ModelCollections",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);

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
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
            table: "Tag");

        migrationBuilder.DropColumn(
            name: "Revision",
            table: "Tag");

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
