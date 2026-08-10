using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class AddAssortmentRowVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "outlet_assortment_override",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "assortment_item",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            /*
             * The backfill, fourth and fifth time — and the first where the counter already exists.
             *
             * `products.change_sequence` was created by `AddProductRowVersion`, and for a tenant with
             * a catalogue it is already well above 1. Stamping these rows at 1 — what every earlier
             * backfill did — would put them *below* the cursor synced devices are already holding, so
             * `RowVersion > cursor` would never match and they would be invisible to exactly the
             * devices that have been working. The opposite of the failure the other backfills feared,
             * reached by copying them.
             *
             * So: make sure every affected tenant has a counter row, advance it once, and stamp both
             * tables at the new value. Everything already stored becomes one change, at a version
             * strictly above anything issued before — which is not historically true and does not
             * need to be. What it guarantees is that every device sees these rows exactly once.
             */
            migrationBuilder.Sql(@"
                INSERT INTO products.change_sequence (""TenantId"", ""Value"")
                SELECT DISTINCT ""TenantId"", 0 FROM products.assortment_item
                UNION
                SELECT DISTINCT ""TenantId"", 0 FROM products.outlet_assortment_override
                ON CONFLICT (""TenantId"") DO NOTHING;");

            migrationBuilder.Sql(@"
                UPDATE products.change_sequence
                SET ""Value"" = ""Value"" + 1
                WHERE ""TenantId"" IN (
                    SELECT ""TenantId"" FROM products.assortment_item
                    UNION
                    SELECT ""TenantId"" FROM products.outlet_assortment_override);");

            migrationBuilder.Sql(@"
                UPDATE products.assortment_item AS item
                SET ""RowVersion"" = sequence.""Value""
                FROM products.change_sequence AS sequence
                WHERE sequence.""TenantId"" = item.""TenantId"";");

            migrationBuilder.Sql(@"
                UPDATE products.outlet_assortment_override AS override
                SET ""RowVersion"" = sequence.""Value""
                FROM products.change_sequence AS sequence
                WHERE sequence.""TenantId"" = override.""TenantId"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "outlet_assortment_override");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "assortment_item");
        }
    }
}
