using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionRowVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "promotion_assignment",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "promotion",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Sixth backfill, second written by the helper. Targets and tiers are not stamped: they
            // travel inside the promotion, so the version that matters is the root's.
            migrationBuilder.Sql(SyncBackfill.Sql("products", "promotion", "promotion_assignment"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "promotion_assignment");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "promotion");
        }
    }
}
