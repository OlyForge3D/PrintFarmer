using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations;

/// <inheritdoc />
public partial class AddLoginAuditLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LoginAuditEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                Success = table.Column<bool>(type: "bit", nullable: false),
                IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                FailureReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LoginAuditEntries", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LoginAuditEntries_Success",
            table: "LoginAuditEntries",
            column: "Success");

        migrationBuilder.CreateIndex(
            name: "IX_LoginAuditEntries_Timestamp",
            table: "LoginAuditEntries",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_LoginAuditEntries_Username",
            table: "LoginAuditEntries",
            column: "Username");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "LoginAuditEntries");
    }
}
