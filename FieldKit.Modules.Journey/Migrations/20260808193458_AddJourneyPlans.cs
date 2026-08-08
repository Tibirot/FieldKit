using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Journey.Migrations
{
    /// <inheritdoc />
    public partial class AddJourneyPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "journey_plan",
                schema: "journey",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GeneratedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journey_plan", x => x.Id);
                    table.UniqueConstraint("AK_journey_plan_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("ck_journey_plan_window", "\"to_date\" >= \"from_date\"");
                });

            migrationBuilder.CreateTable(
                name: "plan_shortfall",
                schema: "journey",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JourneyPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Required = table.Column<int>(type: "integer", nullable: false),
                    Planned = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_shortfall", x => x.Id);
                    table.CheckConstraint("ck_plan_shortfall_is_short", "\"Planned\" < \"Required\"");
                    table.ForeignKey(
                        name: "FK_plan_shortfall_journey_plan_TenantId_JourneyPlanId",
                        columns: x => new { x.TenantId, x.JourneyPlanId },
                        principalSchema: "journey",
                        principalTable: "journey_plan",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planned_visit",
                schema: "journey",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JourneyPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planned_visit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_planned_visit_journey_plan_TenantId_JourneyPlanId",
                        columns: x => new { x.TenantId, x.JourneyPlanId },
                        principalSchema: "journey",
                        principalTable: "journey_plan",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_journey_plan_TenantId_UserId_from_date",
                schema: "journey",
                table: "journey_plan",
                columns: new[] { "TenantId", "UserId", "from_date" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_shortfall_TenantId_JourneyPlanId",
                schema: "journey",
                table: "plan_shortfall",
                columns: new[] { "TenantId", "JourneyPlanId" });

            migrationBuilder.CreateIndex(
                name: "IX_planned_visit_TenantId_JourneyPlanId_Date",
                schema: "journey",
                table: "planned_visit",
                columns: new[] { "TenantId", "JourneyPlanId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plan_shortfall",
                schema: "journey");

            migrationBuilder.DropTable(
                name: "planned_visit",
                schema: "journey");

            migrationBuilder.DropTable(
                name: "journey_plan",
                schema: "journey");
        }
    }
}
