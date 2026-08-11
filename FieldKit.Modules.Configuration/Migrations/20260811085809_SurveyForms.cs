using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class SurveyForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "survey_form",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_form", x => x.Id);
                    table.UniqueConstraint("AK_survey_form_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "survey_question",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveyFormId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    options = table.Column<string[]>(type: "text[]", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_question", x => x.Id);
                    table.CheckConstraint("ck_survey_question_order", "\"Order\" >= 1");
                    table.ForeignKey(
                        name: "FK_survey_question_survey_form_TenantId_SurveyFormId",
                        columns: x => new { x.TenantId, x.SurveyFormId },
                        principalSchema: "config",
                        principalTable: "survey_form",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_survey_form_TenantId_Name",
                schema: "config",
                table: "survey_form",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_question_TenantId_SurveyFormId_Key",
                schema: "config",
                table: "survey_question",
                columns: new[] { "TenantId", "SurveyFormId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_question_TenantId_SurveyFormId_Order",
                schema: "config",
                table: "survey_question",
                columns: new[] { "TenantId", "SurveyFormId", "Order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "survey_question",
                schema: "config");

            migrationBuilder.DropTable(
                name: "survey_form",
                schema: "config");
        }
    }
}
