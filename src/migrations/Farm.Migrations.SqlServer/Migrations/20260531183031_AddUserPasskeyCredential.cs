using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddUserPasskeyCredential : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<DateTime>(
            name: "Timestamp",
            table: "LoginAuditEntries",
            type: "datetime2",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset");

        migrationBuilder.CreateTable(
            name: "UserPasskeyCredentials",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CredentialId = table.Column<byte[]>(type: "varbinary(900)", nullable: false),
                PublicKey = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                SignCount = table.Column<long>(type: "bigint", nullable: false),
                DeviceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                AaguidDescription = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserPasskeyCredentials", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserPasskeyCredentials_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserPasskeyCredentials_CredentialId",
            table: "UserPasskeyCredentials",
            column: "CredentialId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserPasskeyCredentials_UserId",
            table: "UserPasskeyCredentials",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UserPasskeyCredentials");

        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "Timestamp",
            table: "LoginAuditEntries",
            type: "datetimeoffset",
            nullable: false,
            oldClrType: typeof(DateTime),
            oldType: "datetime2");
    }
}
