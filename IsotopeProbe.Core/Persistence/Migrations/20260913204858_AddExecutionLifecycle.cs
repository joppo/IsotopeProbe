using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsotopeProbe.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ExitCode",
                table: "scan_executions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "scan_executions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "scan_executions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "scan_executions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Every historical row was saved only after the runner returned.
            // Preserve its recorded exit code and timestamps; derive only status.
            migrationBuilder.Sql("""
                UPDATE scan_executions
                SET "Status" = CASE WHEN "ExitCode" = 0 THEN 'Succeeded' ELSE 'Failed' END;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "Status", table: "scan_executions",
                type: "character varying(20)", maxLength: 20, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(20)",
                oldMaxLength: 20, oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old schema cannot represent an unfinished or never-started scan.
            // Refuse a lossy downgrade instead of inventing completion data.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM scan_executions
                               WHERE "CompletedAt" IS NULL OR "ExitCode" IS NULL
                                  OR "Status" NOT IN ('Succeeded', 'Failed')
                                  OR ("Status" = 'Succeeded') <> ("ExitCode" = 0)) THEN
                        RAISE EXCEPTION 'Cannot downgrade: the old schema cannot represent existing lifecycle records.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "scan_executions");

            migrationBuilder.AlterColumn<int>(
                name: "ExitCode",
                table: "scan_executions",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "scan_executions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
