using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingRowVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "price_list_line",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "price_list_assignment",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "products",
                table: "price_list",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "price_list_line");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "price_list_assignment");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "products",
                table: "price_list");
        }
    }
}
