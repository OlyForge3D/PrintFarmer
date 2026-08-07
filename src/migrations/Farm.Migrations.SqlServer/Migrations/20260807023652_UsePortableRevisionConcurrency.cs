using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class UsePortableRevisionConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Spools");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "QueueDispatchOutbox");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "QueueDispatchAttempts");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "PrintProjectTemplates");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "PrintProjects");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "PrintProjectFiles");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "PrinterServiceState");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "PrinterDispatchStates");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "OutboxSequenceStates");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "GcodeHarvestQueueItems");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "GcodeHarvestOperations");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "DispatchSettings");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "AppSettingsEntities");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "UserSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Spools",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "QueueDispatchOutbox",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "QueueDispatchAttempts",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PrintProjectTemplates",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PrintProjects",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PrintProjectFiles",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "PrintJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 1L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PrinterServiceState",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Printers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "PrinterDispatchStates",
                type: "bigint",
                nullable: false,
                defaultValue: 1L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "OutboxSequenceStates",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "JobExecutions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "GcodeHarvestQueueItems",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "GcodeHarvestOperations",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "GcodeFiles",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "DispatchSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 1L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "AppSettingsEntities",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.UpdateData(
                table: "OutboxSequenceStates",
                keyColumn: "Id",
                keyValue: 1,
                column: "Revision",
                value: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Spools");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "QueueDispatchOutbox");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "QueueDispatchAttempts");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PrintProjectTemplates");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PrintProjects");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PrintProjectFiles");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PrinterServiceState");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "OutboxSequenceStates");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "GcodeHarvestQueueItems");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "GcodeHarvestOperations");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "GcodeFiles");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "AppSettingsEntities");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "UserSettings",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Spools",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "QueueDispatchOutbox",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "QueueDispatchAttempts",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrintProjectTemplates",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrintProjects",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrintProjectFiles",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "PrintJobs",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 1L);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrintJobs",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrinterServiceState",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Printers",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "PrinterDispatchStates",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 1L);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrinterDispatchStates",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "OutboxSequenceStates",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "JobExecutions",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "GcodeHarvestQueueItems",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "GcodeHarvestOperations",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "GcodeFiles",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "DispatchSettings",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 1L);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "DispatchSettings",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "AppSettingsEntities",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
