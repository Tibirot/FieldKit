using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Org.Migrations
{
    /// <inheritdoc />
    public partial class AddRepAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rep_assignment",
                schema: "org",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TerritoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rep_assignment", x => x.Id);
                    table.CheckConstraint("ck_rep_assignment_period", "\"to_date\" IS NULL OR \"to_date\" >= \"from_date\"");
                    table.ForeignKey(
                        name: "FK_rep_assignment_territory_TerritoryId",
                        column: x => x.TerritoryId,
                        principalSchema: "org",
                        principalTable: "territory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rep_assignment_TenantId_TerritoryId_from_date",
                schema: "org",
                table: "rep_assignment",
                columns: new[] { "TenantId", "TerritoryId", "from_date" });

            migrationBuilder.CreateIndex(
                name: "IX_rep_assignment_TenantId_UserId",
                schema: "org",
                table: "rep_assignment",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_rep_assignment_TerritoryId",
                schema: "org",
                table: "rep_assignment",
                column: "TerritoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rep_assignment",
                schema: "org");
        }
    }
}
