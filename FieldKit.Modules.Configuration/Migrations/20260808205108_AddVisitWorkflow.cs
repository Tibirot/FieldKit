using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldKit.Modules.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "visit_workflow",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    PresenceExpected = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit_workflow", x => x.Id);
                    table.UniqueConstraint("AK_visit_workflow_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "visit_workflow_step",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitWorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit_workflow_step", x => x.Id);
                    table.CheckConstraint("ck_visit_workflow_step_order", "\"Order\" >= 1");
                    table.ForeignKey(
                        name: "FK_visit_workflow_step_visit_workflow_TenantId_VisitWorkflowId",
                        columns: x => new { x.TenantId, x.VisitWorkflowId },
                        principalSchema: "config",
                        principalTable: "visit_workflow",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_visit_workflow_TenantId_ChannelId",
                schema: "config",
                table: "visit_workflow",
                columns: new[] { "TenantId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_visit_workflow_step_TenantId_VisitWorkflowId_Order",
                schema: "config",
                table: "visit_workflow_step",
                columns: new[] { "TenantId", "VisitWorkflowId", "Order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "visit_workflow_step",
                schema: "config");

            migrationBuilder.DropTable(
                name: "visit_workflow",
                schema: "config");
        }
    }
}
