using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsotopeProbe.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NucleiVersion",
                table: "scan_executions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileId",
                table: "scan_executions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileVersion",
                table: "scan_executions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotHash",
                table: "scan_executions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TemplateCount",
                table: "scan_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateSourceVersion",
                table: "scan_executions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NucleiVersion",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "ProfileVersion",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "SnapshotHash",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "TemplateCount",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "TemplateSourceVersion",
                table: "scan_executions");
        }
    }
}
