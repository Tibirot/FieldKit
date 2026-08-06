using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class ProductAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PackSize",
                schema: "products",
                table: "product",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "products",
                table: "product",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "UnitOfMeasure",
                schema: "products",
                table: "product",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PackSize",
                schema: "products",
                table: "product");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "products",
                table: "product");

            migrationBuilder.DropColumn(
                name: "UnitOfMeasure",
                schema: "products",
                table: "product");
        }
    }
}
