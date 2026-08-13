using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Order.Migrations
{
    /// <inheritdoc />
    public partial class OrderTaxAndReprice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepricedAtUtc",
                schema: "ordering",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ServerTaxTotal",
                schema: "ordering",
                table: "Orders",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ServerTotal",
                schema: "ordering",
                table: "Orders",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxTotal",
                schema: "ordering",
                table: "Orders",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "captured_against_price_assignments",
                schema: "ordering",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "captured_against_price_lines",
                schema: "ordering",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "captured_against_price_lists",
                schema: "ordering",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "captured_against_promotion_assignments",
                schema: "ordering",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "captured_against_promotions",
                schema: "ordering",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "captured_against_tax_rates",
                schema: "ordering",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                schema: "ordering",
                table: "OrderLines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepricedAtUtc",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ServerTaxTotal",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ServerTotal",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TaxTotal",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "captured_against_price_assignments",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "captured_against_price_lines",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "captured_against_price_lists",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "captured_against_promotion_assignments",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "captured_against_promotions",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "captured_against_tax_rates",
                schema: "ordering",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                schema: "ordering",
                table: "OrderLines");
        }
    }
}
