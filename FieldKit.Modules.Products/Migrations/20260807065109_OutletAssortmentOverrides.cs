using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class OutletAssortmentOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outlet_assortment_override",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    IsMustStock = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlet_assortment_override", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outlet_assortment_override_product_TenantId_ProductId",
                        columns: x => new { x.TenantId, x.ProductId },
                        principalSchema: "products",
                        principalTable: "product",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outlet_assortment_override_TenantId_OutletId",
                schema: "products",
                table: "outlet_assortment_override",
                columns: new[] { "TenantId", "OutletId" });

            migrationBuilder.CreateIndex(
                name: "IX_outlet_assortment_override_TenantId_OutletId_ProductId",
                schema: "products",
                table: "outlet_assortment_override",
                columns: new[] { "TenantId", "OutletId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outlet_assortment_override_TenantId_ProductId",
                schema: "products",
                table: "outlet_assortment_override",
                columns: new[] { "TenantId", "ProductId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outlet_assortment_override",
                schema: "products");
        }
    }
}
