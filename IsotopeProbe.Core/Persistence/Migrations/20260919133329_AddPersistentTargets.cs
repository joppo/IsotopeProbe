using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IsotopeProbe.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistentTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TargetId",
                table: "scan_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "targets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_targets", x => x.Id);
                    table.UniqueConstraint("AK_targets_Id_OwnerUserId", x => new { x.Id, x.OwnerUserId });
                    table.ForeignKey(
                        name: "FK_targets_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scan_executions_TargetId_OwnerUserId",
                table: "scan_executions",
                columns: new[] { "TargetId", "OwnerUserId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_scan_executions_TargetRequiresOwner",
                table: "scan_executions",
                sql: "\"TargetId\" IS NULL OR \"OwnerUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_targets_OwnerUserId_Url",
                table: "targets",
                columns: new[] { "OwnerUserId", "Url" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_scan_executions_targets_TargetId_OwnerUserId",
                table: "scan_executions",
                columns: new[] { "TargetId", "OwnerUserId" },
                principalTable: "targets",
                principalColumns: new[] { "Id", "OwnerUserId" },
                onDelete: ReferentialAction.Restrict);

            // EF runs this backfill in the migration transaction. All application writers
            // must be stopped until the upgraded binaries are deployed (see README).
            // CreatedAtUtc records this migration, not an inferred original creation time.
            migrationBuilder.Sql("""
                INSERT INTO targets ("OwnerUserId", "Name", "Url", "CreatedAtUtc")
                SELECT "OwnerUserId", "Target" COLLATE "C", "Target" COLLATE "C", CURRENT_TIMESTAMP
                FROM scan_executions
                WHERE "OwnerUserId" IS NOT NULL
                  AND "Target" ~* '^https?://([a-z0-9]([a-z0-9.-]*[a-z0-9])?|\[[0-9a-f:.]+\])(:[0-9]+)?([/?#][^[:space:]]*)?$'
                GROUP BY "OwnerUserId", "Target" COLLATE "C";

                UPDATE scan_executions e SET "TargetId" = t."Id"
                FROM targets t
                WHERE e."OwnerUserId" = t."OwnerUserId" AND e."Target" COLLATE "C" = t."Url";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_scan_executions_targets_TargetId_OwnerUserId",
                table: "scan_executions");

            migrationBuilder.DropTable(
                name: "targets");

            migrationBuilder.DropIndex(
                name: "IX_scan_executions_TargetId_OwnerUserId",
                table: "scan_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_scan_executions_TargetRequiresOwner",
                table: "scan_executions");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "scan_executions");
        }
    }
}
