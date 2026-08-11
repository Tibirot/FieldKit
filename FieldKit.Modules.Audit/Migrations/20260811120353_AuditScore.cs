using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Audit.Migrations
{
    /// <inheritdoc />
    public partial class AuditScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Score",
                schema: "audit",
                table: "audit",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_scored_pillar",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pillar = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    Weight = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_scored_pillar", x => x.Id);
                    table.CheckConstraint("ck_audit_scored_pillar_range", "(\"Percentage\" IS NULL OR (\"Percentage\" >= 0 AND \"Percentage\" <= 100)) AND \"Weight\" >= 0");
                    table.ForeignKey(
                        name: "FK_audit_scored_pillar_audit_TenantId_AuditId",
                        columns: x => new { x.TenantId, x.AuditId },
                        principalSchema: "audit",
                        principalTable: "audit",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_scored_pillar_TenantId_AuditId_Pillar",
                schema: "audit",
                table: "audit_scored_pillar",
                columns: new[] { "TenantId", "AuditId", "Pillar" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_scored_pillar",
                schema: "audit");

            migrationBuilder.DropColumn(
                name: "Score",
                schema: "audit",
                table: "audit");
        }
    }
}
