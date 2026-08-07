using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Products.Migrations
{
    /// <inheritdoc />
    public partial class TaxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tax_rate",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxClassId = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    Percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_rate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tax_rate_tax_class_TenantId_TaxClassId",
                        columns: x => new { x.TenantId, x.TaxClassId },
                        principalSchema: "products",
                        principalTable: "tax_class",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tax_rate_TenantId_TaxClassId_country_code",
                schema: "products",
                table: "tax_rate",
                columns: new[] { "TenantId", "TaxClassId", "country_code" });

            migrationBuilder.CreateIndex(
                name: "IX_tax_rate_TenantId_TaxClassId_country_code_EffectiveFrom",
                schema: "products",
                table: "tax_rate",
                columns: new[] { "TenantId", "TaxClassId", "country_code", "EffectiveFrom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tax_rate",
                schema: "products");
        }
    }
}
