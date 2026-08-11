using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Audit.Migrations
{
    /// <inheritdoc />
    public partial class AuditSurveyAnswersAndPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SurveyFormId",
                schema: "audit",
                table: "audit",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_photo",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    Section = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_photo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_photo_audit_TenantId_AuditId",
                        columns: x => new { x.TenantId, x.AuditId },
                        principalSchema: "audit",
                        principalTable: "audit",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_survey_answer",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    QuestionKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    QuestionText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_survey_answer", x => x.Id);
                    table.CheckConstraint("ck_audit_survey_answer_order", "\"Order\" >= 1");
                    table.ForeignKey(
                        name: "FK_audit_survey_answer_audit_TenantId_AuditId",
                        columns: x => new { x.TenantId, x.AuditId },
                        principalSchema: "audit",
                        principalTable: "audit",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_photo_TenantId_AuditId_ObjectKey",
                schema: "audit",
                table: "audit_photo",
                columns: new[] { "TenantId", "AuditId", "ObjectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_survey_answer_TenantId_AuditId_Order",
                schema: "audit",
                table: "audit_survey_answer",
                columns: new[] { "TenantId", "AuditId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_survey_answer_TenantId_AuditId_QuestionKey",
                schema: "audit",
                table: "audit_survey_answer",
                columns: new[] { "TenantId", "AuditId", "QuestionKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_photo",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "audit_survey_answer",
                schema: "audit");

            migrationBuilder.DropColumn(
                name: "SurveyFormId",
                schema: "audit",
                table: "audit");
        }
    }
}
