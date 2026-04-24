using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.PostgreSQL.Migrations;

/// <inheritdoc />
public partial class AddMaterialClusters : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MaterialClusters",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaterialClusters", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrintQuotas",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                GroupName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                QuotaType = table.Column<int>(type: "integer", nullable: false),
                LimitAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                UsedAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                PeriodType = table.Column<int>(type: "integer", nullable: false),
                PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ResetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrintQuotas", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrintQuotas_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserBalances",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                BalanceAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "USD"),
                LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserBalances", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserBalances_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MaterialClusterMembers",
            columns: table => new
            {
                ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                FilamentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaterialClusterMembers", x => new { x.ClusterId, x.FilamentTypeId });
                table.ForeignKey(
                    name: "FK_MaterialClusterMembers_FilamentTypes_FilamentTypeId",
                    column: x => x.FilamentTypeId,
                    principalTable: "FilamentTypes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MaterialClusterMembers_MaterialClusters_ClusterId",
                    column: x => x.ClusterId,
                    principalTable: "MaterialClusters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "BalanceTransactions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserBalanceId = table.Column<Guid>(type: "uuid", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                TransactionType = table.Column<int>(type: "integer", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uuid", nullable: true),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                PerformedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BalanceTransactions", x => x.Id);
                table.ForeignKey(
                    name: "FK_BalanceTransactions_UserBalances_UserBalanceId",
                    column: x => x.UserBalanceId,
                    principalTable: "UserBalances",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_BalanceTransactions_CreatedAt",
            table: "BalanceTransactions",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_BalanceTransactions_PrintJobId",
            table: "BalanceTransactions",
            column: "PrintJobId");

        migrationBuilder.CreateIndex(
            name: "IX_BalanceTransactions_UserBalanceId",
            table: "BalanceTransactions",
            column: "UserBalanceId");

        migrationBuilder.CreateIndex(
            name: "IX_MaterialClusterMembers_FilamentTypeId",
            table: "MaterialClusterMembers",
            column: "FilamentTypeId");

        migrationBuilder.CreateIndex(
            name: "IX_MaterialClusters_Name",
            table: "MaterialClusters",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrintQuotas_GroupName",
            table: "PrintQuotas",
            column: "GroupName");

        migrationBuilder.CreateIndex(
            name: "IX_PrintQuotas_IsActive",
            table: "PrintQuotas",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_PrintQuotas_ResetAt",
            table: "PrintQuotas",
            column: "ResetAt");

        migrationBuilder.CreateIndex(
            name: "IX_PrintQuotas_UserId",
            table: "PrintQuotas",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_UserBalances_UserId",
            table: "UserBalances",
            column: "UserId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BalanceTransactions");

        migrationBuilder.DropTable(
            name: "MaterialClusterMembers");

        migrationBuilder.DropTable(
            name: "PrintQuotas");

        migrationBuilder.DropTable(
            name: "UserBalances");

        migrationBuilder.DropTable(
            name: "MaterialClusters");
    }
}
