using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class PromotionScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "promotion_assignment",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PromotionId = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outlet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_assignment", x => x.Id);
                    table.CheckConstraint("ck_promotion_assignment_one_scope", "(\"channel_id\" IS NULL) <> (\"outlet_id\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_promotion_assignment_promotion_TenantId_PromotionId",
                        columns: x => new { x.TenantId, x.PromotionId },
                        principalSchema: "products",
                        principalTable: "promotion",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_assignment_TenantId_channel_id",
                schema: "products",
                table: "promotion_assignment",
                columns: new[] { "TenantId", "channel_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_assignment_TenantId_outlet_id",
                schema: "products",
                table: "promotion_assignment",
                columns: new[] { "TenantId", "outlet_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_assignment_TenantId_PromotionId_channel_id",
                schema: "products",
                table: "promotion_assignment",
                columns: new[] { "TenantId", "PromotionId", "channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_assignment_TenantId_PromotionId_outlet_id",
                schema: "products",
                table: "promotion_assignment",
                columns: new[] { "TenantId", "PromotionId", "outlet_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_assignment",
                schema: "products");
        }
    }
}
