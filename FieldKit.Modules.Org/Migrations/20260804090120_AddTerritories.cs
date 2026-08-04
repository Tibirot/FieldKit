using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Org.Migrations
{
    /// <inheritdoc />
    public partial class AddTerritories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "territory",
                schema: "org",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_territory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_territory_org_unit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalSchema: "org",
                        principalTable: "org_unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "territory_outlet",
                schema: "org",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TerritoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_territory_outlet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_territory_outlet_territory_TerritoryId",
                        column: x => x.TerritoryId,
                        principalSchema: "org",
                        principalTable: "territory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_territory_OrgUnitId",
                schema: "org",
                table: "territory",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_territory_TenantId_Name",
                schema: "org",
                table: "territory",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_territory_TenantId_OrgUnitId",
                schema: "org",
                table: "territory",
                columns: new[] { "TenantId", "OrgUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_territory_outlet_TenantId_OutletId",
                schema: "org",
                table: "territory_outlet",
                columns: new[] { "TenantId", "OutletId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_territory_outlet_TenantId_TerritoryId",
                schema: "org",
                table: "territory_outlet",
                columns: new[] { "TenantId", "TerritoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_territory_outlet_TerritoryId",
                schema: "org",
                table: "territory_outlet",
                column: "TerritoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "territory_outlet",
                schema: "org");

            migrationBuilder.DropTable(
                name: "territory",
                schema: "org");
        }
    }
}
