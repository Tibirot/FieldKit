using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Outlets.Migrations
{
    /// <inheritdoc />
    public partial class AddOutletCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfilled to an empty JSON object rather than EF's generated "" — hand-edited, and the
            // same trap the TimeZoneId column fell into in #56.
            //
            // An empty string is not valid JSON, so every outlet predating this column would have
            // thrown on deserialization the first time anything read it: a failure with a delay fuse,
            // invisible until the next request. `{}` is what "no custom fields" actually means.
            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                schema: "outlets",
                table: "outlet",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "custom_fields",
                schema: "outlets",
                table: "outlet");
        }
    }
}
