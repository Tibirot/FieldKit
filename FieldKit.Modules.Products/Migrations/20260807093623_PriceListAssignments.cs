using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class PriceListAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "price_list_assignment",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_price_list_assignment", x => x.Id);
                    table.CheckConstraint("ck_price_list_assignment_one_scope", "(\"channel_id\" IS NULL) <> (\"outlet_id\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_price_list_assignment_price_list_TenantId_PriceListId",
                        columns: x => new { x.TenantId, x.PriceListId },
                        principalSchema: "products",
                        principalTable: "price_list",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_price_list_assignment_TenantId_channel_id",
                schema: "products",
                table: "price_list_assignment",
                columns: new[] { "TenantId", "channel_id" });

            migrationBuilder.CreateIndex(
                name: "IX_price_list_assignment_TenantId_outlet_id",
                schema: "products",
                table: "price_list_assignment",
                columns: new[] { "TenantId", "outlet_id" });

            migrationBuilder.CreateIndex(
                name: "IX_price_list_assignment_TenantId_PriceListId_channel_id",
                schema: "products",
                table: "price_list_assignment",
                columns: new[] { "TenantId", "PriceListId", "channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_price_list_assignment_TenantId_PriceListId_outlet_id",
                schema: "products",
                table: "price_list_assignment",
                columns: new[] { "TenantId", "PriceListId", "outlet_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_list_assignment",
                schema: "products");
        }
    }
}
