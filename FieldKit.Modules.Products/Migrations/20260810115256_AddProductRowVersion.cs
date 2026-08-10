using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class AddProductRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "product",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "change_sequence",
                schema: "products",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_sequence", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "tombstone",
                schema: "products",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tombstone", x => new { x.TenantId, x.EntityType, x.EntityId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_tombstone_TenantId_RowVersion",
                schema: "products",
                table: "tombstone",
                columns: new[] { "TenantId", "RowVersion" });

            /*
             * Existing products are stamped, because the default of zero would hide them forever.
             *
             * Third time this migration has been written in three slices, and the consequence is
             * different every time. Here it is a device that holds an empty catalogue: a rep can
             * check in, work the visit, and be unable to name a single thing on the shelf — which
             * reads as a broken app rather than as stale data.
             *
             * One version for the lot: these rows predate the counter, so there is no order among
             * them worth preserving, and it means one page rather than N for a catalogue that could
             * be thousands of rows.
             */
            migrationBuilder.Sql(@"UPDATE products.product SET ""RowVersion"" = 1;");

            migrationBuilder.Sql(@"
                INSERT INTO products.change_sequence (""TenantId"", ""Value"")
                SELECT DISTINCT ""TenantId"", 1 FROM products.product
                ON CONFLICT (""TenantId"") DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_sequence",
                schema: "products");

            migrationBuilder.DropTable(
                name: "tombstone",
                schema: "products");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "product");
        }
    }
}
