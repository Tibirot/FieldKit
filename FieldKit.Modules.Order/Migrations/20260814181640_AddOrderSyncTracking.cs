using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Order.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderSyncTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                schema: "ordering",
                table: "Orders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "change_sequence",
                schema: "ordering",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_sequence", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "tombstone",
                schema: "ordering",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tombstone", x => new { x.TenantId, x.EntityType, x.EntityId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_UserId_RowVersion",
                schema: "ordering",
                table: "Orders",
                columns: new[] { "TenantId", "UserId", "RowVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_tombstone_TenantId_RowVersion",
                schema: "ordering",
                table: "tombstone",
                columns: new[] { "TenantId", "RowVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_sequence",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "tombstone",
                schema: "ordering");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId_UserId_RowVersion",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "ordering",
                table: "Orders");
        }
    }
}
