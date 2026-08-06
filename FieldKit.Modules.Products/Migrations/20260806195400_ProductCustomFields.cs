using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class ProductCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-corrected from the generated `defaultValue: ""`, which is not JSON. Every existing
            // product would take that empty string, and the failure would surface as a
            // deserialization exception the first time anything read the row — a delay fuse,
            // invisible until the next request rather than at migration time. `{}` is what "no
            // custom fields" actually means.
            //
            // Outlets hit exactly this in AddOutletCustomFields and corrected it the same way. That
            // it recurs is the argument for the comment: EF will generate `""` again for the next
            // entity that carries custom fields.
            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                schema: "products",
                table: "product",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "custom_fields",
                schema: "products",
                table: "product");
        }
    }
}
