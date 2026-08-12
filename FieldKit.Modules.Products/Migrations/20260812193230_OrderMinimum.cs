using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class OrderMinimum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_minimum",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outlet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_minimum", x => x.Id);
                    table.CheckConstraint("ck_order_minimum_one_scope", "(\"channel_id\" IS NULL) <> (\"outlet_id\" IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_minimum_TenantId_channel_id",
                schema: "products",
                table: "order_minimum",
                columns: new[] { "TenantId", "channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_minimum_TenantId_outlet_id",
                schema: "products",
                table: "order_minimum",
                columns: new[] { "TenantId", "outlet_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_minimum",
                schema: "products");
        }
    }
}
