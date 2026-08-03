using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Org.Migrations
{
    /// <inheritdoc />
    public partial class AddPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "position",
                schema: "org",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position", x => x.Id);
                    table.ForeignKey(
                        name: "FK_position_org_unit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalSchema: "org",
                        principalTable: "org_unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_position_OrgUnitId",
                schema: "org",
                table: "position",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_position_TenantId_OrgUnitId",
                schema: "org",
                table: "position",
                columns: new[] { "TenantId", "OrgUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_position_TenantId_UserId_OrgUnitId",
                schema: "org",
                table: "position",
                columns: new[] { "TenantId", "UserId", "OrgUnitId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "position",
                schema: "org");
        }
    }
}
