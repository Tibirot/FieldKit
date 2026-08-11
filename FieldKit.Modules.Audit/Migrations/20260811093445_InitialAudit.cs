using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Audit.Migrations
{
    /// <inheritdoc />
    public partial class InitialAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "audit",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WeightSetVersion = table.Column<int>(type: "integer", nullable: false),
                    CategoryFacings = table.Column<int>(type: "integer", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit", x => x.Id);
                    table.UniqueConstraint("AK_audit_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("ck_audit_category_facings", "\"CategoryFacings\" IS NULL OR \"CategoryFacings\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Content = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredOnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedOnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_message", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_availability",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_availability", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_availability_audit_TenantId_AuditId",
                        columns: x => new { x.TenantId, x.AuditId },
                        principalSchema: "audit",
                        principalTable: "audit",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_facings",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Facings = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_facings", x => x.Id);
                    table.CheckConstraint("ck_audit_facings_count", "\"Facings\" >= 0");
                    table.ForeignKey(
                        name: "FK_audit_facings_audit_TenantId_AuditId",
                        columns: x => new { x.TenantId, x.AuditId },
                        principalSchema: "audit",
                        principalTable: "audit",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_price",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_price", x => x.Id);
                    table.CheckConstraint("ck_audit_price_amounts", "\"ObservedMinorUnits\" >= 0 AND (\"ExpectedMinorUnits\" IS NULL OR \"ExpectedMinorUnits\" >= 0)");
                    table.ForeignKey(
                        name: "FK_audit_price_audit_TenantId_AuditId",
                        columns: x => new { x.TenantId, x.AuditId },
                        principalSchema: "audit",
                        principalTable: "audit",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_TenantId_OutletId_CapturedAtUtc",
                schema: "audit",
                table: "audit",
                columns: new[] { "TenantId", "OutletId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_TenantId_VisitId",
                schema: "audit",
                table: "audit",
                columns: new[] { "TenantId", "VisitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_availability_TenantId_AuditId_ProductId",
                schema: "audit",
                table: "audit_availability",
                columns: new[] { "TenantId", "AuditId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_facings_TenantId_AuditId_ProductId",
                schema: "audit",
                table: "audit_facings",
                columns: new[] { "TenantId", "AuditId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_price_TenantId_AuditId_ProductId",
                schema: "audit",
                table: "audit_price",
                columns: new[] { "TenantId", "AuditId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_message_ProcessedOnUtc",
                schema: "audit",
                table: "outbox_message",
                column: "ProcessedOnUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_availability",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "audit_facings",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "audit_price",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "audit",
                schema: "audit");
        }
    }
}
