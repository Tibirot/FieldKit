using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class PromotionTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_promotion_value_matches_type",
                schema: "products",
                table: "promotion");

            migrationBuilder.CreateTable(
                name: "promotion_tier",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PromotionId = table.Column<Guid>(type: "uuid", nullable: false),
                    min_quantity = table.Column<int>(type: "integer", nullable: false),
                    percent_off = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    amount_off = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_tier", x => x.Id);
                    table.CheckConstraint("ck_promotion_tier_value", "((\"percent_off\" IS NULL) <> (\"amount_off\" IS NULL)) AND ((\"amount_off\" IS NULL) = (\"currency\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_promotion_tier_promotion_TenantId_PromotionId",
                        columns: x => new { x.TenantId, x.PromotionId },
                        principalSchema: "products",
                        principalTable: "promotion",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_promotion_value_matches_type",
                schema: "products",
                table: "promotion",
                sql: "CASE \"type\" WHEN 'PercentOff' THEN \"percent_off\" IS NOT NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL WHEN 'FixedAmountOff' THEN \"amount_off\" IS NOT NULL AND \"currency\" IS NOT NULL AND \"percent_off\" IS NULL WHEN 'VolumeTiered' THEN \"percent_off\" IS NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL ELSE TRUE END");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_tier_TenantId_PromotionId_min_quantity",
                schema: "products",
                table: "promotion_tier",
                columns: new[] { "TenantId", "PromotionId", "min_quantity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_tier",
                schema: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_promotion_value_matches_type",
                schema: "products",
                table: "promotion");

            migrationBuilder.AddCheckConstraint(
                name: "ck_promotion_value_matches_type",
                schema: "products",
                table: "promotion",
                sql: "CASE \"type\" WHEN 'PercentOff' THEN \"percent_off\" IS NOT NULL AND \"amount_off\" IS NULL AND \"currency\" IS NULL WHEN 'FixedAmountOff' THEN \"amount_off\" IS NOT NULL AND \"currency\" IS NOT NULL AND \"percent_off\" IS NULL ELSE TRUE END");
        }
    }
}
