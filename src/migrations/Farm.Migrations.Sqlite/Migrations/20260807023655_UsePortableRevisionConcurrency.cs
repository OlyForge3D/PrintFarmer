using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class UsePortableRevisionConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"PrintJobs\" SET \"Revision\" = 1 WHERE \"Revision\" < 1;");
            migrationBuilder.Sql(
                "UPDATE \"PrinterDispatchStates\" SET \"Revision\" = 1 WHERE \"Revision\" < 1;");
            migrationBuilder.Sql(
                "UPDATE \"DispatchSettings\" SET \"Revision\" = 1 WHERE \"Revision\" < 1;");

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
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Spools",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "QueueDispatchOutbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "QueueDispatchAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PrintProjectTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PrintProjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PrintProjectFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "PrintJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PrinterServiceState",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Printers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "PrinterDispatchStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "OutboxSequenceStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "JobExecutions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "GcodeHarvestQueueItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "GcodeHarvestOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "GcodeFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "DispatchSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "AppSettingsEntities",
                type: "INTEGER",
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
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Spools",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "QueueDispatchOutbox",
                type: "BLOB",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "QueueDispatchAttempts",
                type: "BLOB",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrintProjectTemplates",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrintProjects",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrintProjectFiles",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "PrintJobs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1L);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrintJobs",
                type: "BLOB",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrinterServiceState",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Printers",
                type: "BLOB",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "PrinterDispatchStates",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1L);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "PrinterDispatchStates",
                type: "BLOB",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "OutboxSequenceStates",
                type: "BLOB",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "JobExecutions",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "GcodeHarvestQueueItems",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "GcodeHarvestOperations",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "GcodeFiles",
                type: "BLOB",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "DispatchSettings",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1L);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "DispatchSettings",
                type: "BLOB",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "AppSettingsEntities",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.UpdateData(
                table: "DispatchSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "RowVersion",
                value: null);

            migrationBuilder.UpdateData(
                table: "OutboxSequenceStates",
                keyColumn: "Id",
                keyValue: 1,
                column: "RowVersion",
                value: null);
        }
    }
}
