using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class TaxRateRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "tax_rate",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Seventh backfill. Every existing rate is stamped so a device's first pull carries the
            // whole table rather than nothing: a zero-defaulted column is never `> cursor` for a
            // cursor of zero, so without this the rates would exist on the server and never travel.
            migrationBuilder.Sql(SyncBackfill.Sql("products", "tax_rate"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "tax_rate");
        }
    }
}

