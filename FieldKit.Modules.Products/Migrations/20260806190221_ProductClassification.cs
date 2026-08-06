using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class ProductClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BrandId",
                schema: "products",
                table: "product",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                schema: "products",
                table: "product",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxClassId",
                schema: "products",
                table: "product",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_tax_class_TenantId_Id",
                schema: "products",
                table: "tax_class",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_brand_TenantId_Id",
                schema: "products",
                table: "brand",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_product_TenantId_BrandId",
                schema: "products",
                table: "product",
                columns: new[] { "TenantId", "BrandId" });

            migrationBuilder.CreateIndex(
                name: "IX_product_TenantId_CategoryId",
                schema: "products",
                table: "product",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_product_TenantId_TaxClassId",
                schema: "products",
                table: "product",
                columns: new[] { "TenantId", "TaxClassId" });

            migrationBuilder.AddForeignKey(
                name: "FK_product_brand_TenantId_BrandId",
                schema: "products",
                table: "product",
                columns: new[] { "TenantId", "BrandId" },
                principalSchema: "products",
                principalTable: "brand",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_category_TenantId_CategoryId",
                schema: "products",
                table: "product",
                columns: new[] { "TenantId", "CategoryId" },
                principalSchema: "products",
                principalTable: "category",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_tax_class_TenantId_TaxClassId",
                schema: "products",
                table: "product",
                columns: new[] { "TenantId", "TaxClassId" },
                principalSchema: "products",
                principalTable: "tax_class",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_product_brand_TenantId_BrandId",
                schema: "products",
                table: "product");

            migrationBuilder.DropForeignKey(
                name: "FK_product_category_TenantId_CategoryId",
                schema: "products",
                table: "product");

            migrationBuilder.DropForeignKey(
                name: "FK_product_tax_class_TenantId_TaxClassId",
                schema: "products",
                table: "product");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_tax_class_TenantId_Id",
                schema: "products",
                table: "tax_class");

            migrationBuilder.DropIndex(
                name: "IX_product_TenantId_BrandId",
                schema: "products",
                table: "product");

            migrationBuilder.DropIndex(
                name: "IX_product_TenantId_CategoryId",
                schema: "products",
                table: "product");

            migrationBuilder.DropIndex(
                name: "IX_product_TenantId_TaxClassId",
                schema: "products",
                table: "product");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_brand_TenantId_Id",
                schema: "products",
                table: "brand");

            migrationBuilder.DropColumn(
                name: "BrandId",
                schema: "products",
                table: "product");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                schema: "products",
                table: "product");

            migrationBuilder.DropColumn(
                name: "TaxClassId",
                schema: "products",
                table: "product");
        }
    }
}
