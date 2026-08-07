using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class BuyXGetY : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_promotion_value_matches_type",
                schema: "products",
                table: "promotion");

            migrationBuilder.AddColumn<int>(
                name: "buy_quantity",
                schema: "products",
                table: "promotion",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "get_percent_off",
                schema: "products",
                table: "promotion",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "get_product_id",
                schema: "products",
                table: "promotion",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "get_quantity",
                schema: "products",
                table: "promotion",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_TenantId_get_product_id",
                schema: "products",
                table: "promotion",
                columns: new[] { "TenantId", "get_product_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_promotion_value_matches_type",
                schema: "products",
                table: "promotion",
                sql: "CASE \"type\" WHEN 'PercentOff' THEN \"percent_off\" IS NOT NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL AND \"buy_quantity\" IS NULL AND \"get_quantity\" IS NULL AND \"get_percent_off\" IS NULL WHEN 'FixedAmountOff' THEN \"amount_off\" IS NOT NULL AND \"currency\" IS NOT NULL AND \"percent_off\" IS NULL AND \"buy_quantity\" IS NULL AND \"get_quantity\" IS NULL AND \"get_percent_off\" IS NULL WHEN 'VolumeTiered' THEN \"percent_off\" IS NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL AND \"buy_quantity\" IS NULL AND \"get_quantity\" IS NULL AND \"get_percent_off\" IS NULL WHEN 'BuyXGetY' THEN \"percent_off\" IS NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL AND \"buy_quantity\" IS NOT NULL AND \"get_quantity\" IS NOT NULL AND \"get_percent_off\" IS NOT NULL ELSE FALSE END");

            migrationBuilder.AddForeignKey(
                name: "FK_promotion_product_TenantId_get_product_id",
                schema: "products",
                table: "promotion",
                columns: new[] { "TenantId", "get_product_id" },
                principalSchema: "products",
                principalTable: "product",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_promotion_product_TenantId_get_product_id",
                schema: "products",
                table: "promotion");

            migrationBuilder.DropIndex(
                name: "IX_promotion_TenantId_get_product_id",
                schema: "products",
                table: "promotion");

            migrationBuilder.DropCheckConstraint(
                name: "ck_promotion_value_matches_type",
                schema: "products",
                table: "promotion");

            migrationBuilder.DropColumn(
                name: "buy_quantity",
                schema: "products",
                table: "promotion");

            migrationBuilder.DropColumn(
                name: "get_percent_off",
                schema: "products",
                table: "promotion");

            migrationBuilder.DropColumn(
                name: "get_product_id",
                schema: "products",
                table: "promotion");

            migrationBuilder.DropColumn(
                name: "get_quantity",
                schema: "products",
                table: "promotion");

            migrationBuilder.AddCheckConstraint(
                name: "ck_promotion_value_matches_type",
                schema: "products",
                table: "promotion",
                sql: "CASE \"type\" WHEN 'PercentOff' THEN \"percent_off\" IS NOT NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL WHEN 'FixedAmountOff' THEN \"amount_off\" IS NOT NULL AND \"currency\" IS NOT NULL AND \"percent_off\" IS NULL WHEN 'VolumeTiered' THEN \"percent_off\" IS NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL ELSE TRUE END");
        }
    }
}
