using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsotopeProbe.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebScanQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_scan_executions_OwnerUserId",
                table: "scan_executions");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "scan_executions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EnqueuedAt",
                table: "scan_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "scan_executions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Cli");

            migrationBuilder.AddColumn<Guid>(
                name: "SubmissionId",
                table: "scan_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplatePath",
                table: "scan_executions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateProfile",
                table: "scan_executions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "scan_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_scan_executions_OwnerUserId_SubmissionId",
                table: "scan_executions",
                columns: new[] { "OwnerUserId", "SubmissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scan_executions_Source_Status_EnqueuedAt_Id",
                table: "scan_executions",
                columns: new[] { "Source", "Status", "EnqueuedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_scan_executions_OwnerUserId_SubmissionId",
                table: "scan_executions");

            migrationBuilder.DropIndex(
                name: "IX_scan_executions_Source_Status_EnqueuedAt_Id",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "EnqueuedAt",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "SubmissionId",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "TemplatePath",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "TemplateProfile",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "scan_executions");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "scan_executions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_scan_executions_OwnerUserId",
                table: "scan_executions",
                column: "OwnerUserId");
        }
    }
}
