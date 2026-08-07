using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class Promotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "promotion",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    percent_off = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    amount_off = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion", x => x.Id);
                    table.UniqueConstraint("AK_promotion_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("ck_promotion_value_matches_type", "CASE \"type\" WHEN 'PercentOff' THEN \"percent_off\" IS NOT NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL WHEN 'FixedAmountOff' THEN \"amount_off\" IS NOT NULL AND \"currency\" IS NOT NULL AND \"percent_off\" IS NULL ELSE TRUE END");
                });

            migrationBuilder.CreateTable(
                name: "promotion_target",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PromotionId = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_target", x => x.Id);
                    table.CheckConstraint("ck_promotion_target_one_subject", "(\"product_id\" IS NULL) <> (\"category_id\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_promotion_target_category_TenantId_category_id",
                        columns: x => new { x.TenantId, x.category_id },
                        principalSchema: "products",
                        principalTable: "category",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_promotion_target_product_TenantId_product_id",
                        columns: x => new { x.TenantId, x.product_id },
                        principalSchema: "products",
                        principalTable: "product",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_promotion_target_promotion_TenantId_PromotionId",
                        columns: x => new { x.TenantId, x.PromotionId },
                        principalSchema: "products",
                        principalTable: "promotion",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_TenantId_Name",
                schema: "products",
                table: "promotion",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_TenantId_ValidFrom_Priority",
                schema: "products",
                table: "promotion",
                columns: new[] { "TenantId", "ValidFrom", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_target_TenantId_category_id",
                schema: "products",
                table: "promotion_target",
                columns: new[] { "TenantId", "category_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_target_TenantId_product_id",
                schema: "products",
                table: "promotion_target",
                columns: new[] { "TenantId", "product_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_target_TenantId_PromotionId_category_id",
                schema: "products",
                table: "promotion_target",
                columns: new[] { "TenantId", "PromotionId", "category_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_target_TenantId_PromotionId_product_id",
                schema: "products",
                table: "promotion_target",
                columns: new[] { "TenantId", "PromotionId", "product_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_target",
                schema: "products");

            migrationBuilder.DropTable(
                name: "promotion",
                schema: "products");
        }
    }
}
