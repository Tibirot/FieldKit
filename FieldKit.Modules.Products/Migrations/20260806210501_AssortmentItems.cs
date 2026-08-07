using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class AssortmentItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_product_TenantId_Id",
                schema: "products",
                table: "product",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "assortment_item",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsMustStock = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assortment_item", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assortment_item_product_TenantId_ProductId",
                        columns: x => new { x.TenantId, x.ProductId },
                        principalSchema: "products",
                        principalTable: "product",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assortment_item_TenantId_ChannelId_IsMustStock",
                schema: "products",
                table: "assortment_item",
                columns: new[] { "TenantId", "ChannelId", "IsMustStock" });

            migrationBuilder.CreateIndex(
                name: "IX_assortment_item_TenantId_ChannelId_ProductId",
                schema: "products",
                table: "assortment_item",
                columns: new[] { "TenantId", "ChannelId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assortment_item_TenantId_ProductId",
                schema: "products",
                table: "assortment_item",
                columns: new[] { "TenantId", "ProductId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assortment_item",
                schema: "products");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_product_TenantId_Id",
                schema: "products",
                table: "product");
        }
    }
}
