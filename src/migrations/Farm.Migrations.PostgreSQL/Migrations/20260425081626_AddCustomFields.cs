using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddCustomFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CustomFieldDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EntityType = table.Column<int>(type: "integer", nullable: false),
                FieldName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                FieldKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                FieldType = table.Column<int>(type: "integer", nullable: false),
                Options = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                DefaultValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomFieldDefinitions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CustomFieldValues",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                CreatedDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomFieldValues", x => x.Id);
                table.ForeignKey(
                    name: "FK_CustomFieldValues_CustomFieldDefinitions_DefinitionId",
                    column: x => x.DefinitionId,
                    principalTable: "CustomFieldDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CustomFieldDefinitions_EntityType_FieldKey",
            table: "CustomFieldDefinitions",
            columns: new[] { "EntityType", "FieldKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CustomFieldValues_DefinitionId_EntityId",
            table: "CustomFieldValues",
            columns: new[] { "DefinitionId", "EntityId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CustomFieldValues");

        migrationBuilder.DropTable(
            name: "CustomFieldDefinitions");
    }
}
