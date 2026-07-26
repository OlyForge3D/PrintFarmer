using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Farm.Slicer.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AlignDevelopmentSlicerSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExtruderFilamentProfileNamesJson",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ModelFileTransformsJson",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ModelFileUrlsJson",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ModelTransformJson",
            table: "SliceJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ClientUploadHash",
            table: "Models3D",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ClientUploadId",
            table: "Models3D",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExtractedMetadataJson",
            table: "Models3D",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ImportedAt",
            table: "Models3D",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceCreator",
            table: "Models3D",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceLicense",
            table: "Models3D",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceUrl",
            table: "Models3D",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Models3D_UploadedByUserId_ClientUploadId",
            table: "Models3D",
            columns: new[] { "UploadedByUserId", "ClientUploadId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Models3D_UploadedByUserId_ClientUploadId",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "ExtruderFilamentProfileNamesJson",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ModelFileTransformsJson",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ModelFileUrlsJson",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ModelTransformJson",
            table: "SliceJobs");

        migrationBuilder.DropColumn(
            name: "ClientUploadHash",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "ClientUploadId",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "ExtractedMetadataJson",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "ImportedAt",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "SourceCreator",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "SourceLicense",
            table: "Models3D");

        migrationBuilder.DropColumn(
            name: "SourceUrl",
            table: "Models3D");
    }
}
