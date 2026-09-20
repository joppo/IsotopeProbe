using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsotopeProbe.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatcherName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatcherName",
                table: "findings",
                type: "text",
                nullable: true);

            // RawJson is jsonb, so syntactically invalid JSON cannot be stored. Operators
            // safely return NULL for unsuitable shapes. Match .NET whitespace semantics.
            migrationBuilder.Sql("""
                UPDATE findings SET "MatcherName" = "RawJson" ->> 'matcher-name'
                WHERE "MatcherName" IS NULL
                  AND jsonb_typeof("RawJson" -> 'matcher-name') = 'string'
                  AND length(btrim("RawJson" ->> 'matcher-name',
                    U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000')) > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatcherName",
                table: "findings");
        }
    }
}
