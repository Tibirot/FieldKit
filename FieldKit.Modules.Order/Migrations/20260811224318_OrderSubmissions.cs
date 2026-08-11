using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Order.Migrations
{
    /// <inheritdoc />
    public partial class OrderSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderSubmission",
                schema: "ordering",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    MutationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderSubmission", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderSubmission_Orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "ordering",
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderSubmission_OrderId",
                schema: "ordering",
                table: "OrderSubmission",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderSubmission_TenantId_MutationId",
                schema: "ordering",
                table: "OrderSubmission",
                columns: new[] { "TenantId", "MutationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderSubmission_TenantId_OrderId_SubmittedAtUtc",
                schema: "ordering",
                table: "OrderSubmission",
                columns: new[] { "TenantId", "OrderId", "SubmittedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderSubmission",
                schema: "ordering");
        }
    }
}
