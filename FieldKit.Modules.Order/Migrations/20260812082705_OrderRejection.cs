using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Order.Migrations
{
    /// <inheritdoc />
    public partial class OrderRejection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                schema: "ordering",
                table: "OrderSubmission",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OffendingProductId",
                schema: "ordering",
                table: "OrderSubmission",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                schema: "ordering",
                table: "OrderSubmission",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RejectionReason",
                schema: "ordering",
                table: "OrderSubmission",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Note",
                schema: "ordering",
                table: "OrderSubmission");

            migrationBuilder.DropColumn(
                name: "OffendingProductId",
                schema: "ordering",
                table: "OrderSubmission");

            migrationBuilder.DropColumn(
                name: "Outcome",
                schema: "ordering",
                table: "OrderSubmission");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                schema: "ordering",
                table: "OrderSubmission");
        }
    }
}
