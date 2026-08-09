using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Visit.Migrations
{
    /// <inheritdoc />
    public partial class VisitSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "visit_step",
                schema: "visit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit_step", x => x.Id);
                    table.CheckConstraint("ck_visit_step_completed_at", "(\"Status\" = 'Completed') = (\"CompletedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("ck_visit_step_note_text", "\"Type\" <> 'Note' OR \"Status\" <> 'Completed' OR \"Notes\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_visit_step_visit_VisitId",
                        column: x => x.VisitId,
                        principalSchema: "visit",
                        principalTable: "visit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_visit_step_VisitId",
                schema: "visit",
                table: "visit_step",
                column: "VisitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "visit_step",
                schema: "visit");
        }
    }
}
