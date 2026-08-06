using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Org.Migrations
{
    /// <inheritdoc />
    public partial class TenantKeyOrgUnitParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_org_unit_org_unit_ParentId",
                schema: "org",
                table: "org_unit");

            migrationBuilder.DropIndex(
                name: "IX_org_unit_ParentId",
                schema: "org",
                table: "org_unit");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_org_unit_TenantId_Id",
                schema: "org",
                table: "org_unit",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_org_unit_org_unit_TenantId_ParentId",
                schema: "org",
                table: "org_unit",
                columns: new[] { "TenantId", "ParentId" },
                principalSchema: "org",
                principalTable: "org_unit",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_org_unit_org_unit_TenantId_ParentId",
                schema: "org",
                table: "org_unit");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_org_unit_TenantId_Id",
                schema: "org",
                table: "org_unit");

            migrationBuilder.CreateIndex(
                name: "IX_org_unit_ParentId",
                schema: "org",
                table: "org_unit",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_org_unit_org_unit_ParentId",
                schema: "org",
                table: "org_unit",
                column: "ParentId",
                principalSchema: "org",
                principalTable: "org_unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
