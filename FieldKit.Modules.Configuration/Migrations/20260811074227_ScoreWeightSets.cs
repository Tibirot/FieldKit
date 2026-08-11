using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class ScoreWeightSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "score_weight_set",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_score_weight_set", x => x.Id);
                    table.UniqueConstraint("AK_score_weight_set_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("ck_score_weight_set_version", "\"Version\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "score_weight",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScoreWeightSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pillar = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_score_weight", x => x.Id);
                    table.CheckConstraint("ck_score_weight_percentage", "\"Percentage\" >= 0 AND \"Percentage\" <= 100");
                    table.ForeignKey(
                        name: "FK_score_weight_score_weight_set_TenantId_ScoreWeightSetId",
                        columns: x => new { x.TenantId, x.ScoreWeightSetId },
                        principalSchema: "config",
                        principalTable: "score_weight_set",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_score_weight_TenantId_ScoreWeightSetId_Pillar",
                schema: "config",
                table: "score_weight",
                columns: new[] { "TenantId", "ScoreWeightSetId", "Pillar" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_score_weight_set_TenantId_PublishedAtUtc",
                schema: "config",
                table: "score_weight_set",
                columns: new[] { "TenantId", "PublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_score_weight_set_TenantId_Version",
                schema: "config",
                table: "score_weight_set",
                columns: new[] { "TenantId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "score_weight",
                schema: "config");

            migrationBuilder.DropTable(
                name: "score_weight_set",
                schema: "config");
        }
    }
}
